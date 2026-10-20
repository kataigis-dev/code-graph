"""
Grafo prodotto da roslyn-graph: caricamento, dossier delle rotte (trace), sorgente e contesto per l'LLM.

Lo usano il generatore della documentazione Markdown (graph_docs.py) e l'arricchimento con LLM (enrich.py).
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
import os
import re
import sys
from collections import deque
from typing import Any, Iterable

import networkx as nx

CALL_EDGES = ("CALLS", "DISPATCHES_TO")
DATA_EDGES = ("READS", "WRITES", "CALLS_PROC")
# flusso di esecuzione: chiamate piu' i collegamenti Web Forms (pagina -> gestori di evento, controlli utente, master page)
FLOW_EDGES = CALL_EDGES + ("HANDLES_EVENT", "USES_CONTROL", "USES_MASTER")
DB_LOGIC_KINDS = ("StoredProcedure", "Trigger")
# entry point raggiungibili dalla rete: per gli altri (batch, job, servizi, messaggi, eventi) l'autorizzazione non si applica
NETWORK_FLAVORS = {"mvc", "webapi", "aspnetcore", "convention", "webforms", "asmx", "ashx", "httphandler", "wcf", "signalr"}
# integrazioni che in un test vanno sostituite (server SMTP di prova, stub HTTP, code locali)
SIDE_EFFECTS = {"email", "http", "wcf", "msmq", "rabbitmq", "servicebus", "azure-messaging", "filesystem", "ldap", "cache"}
VALIDATION_ATTRS = re.compile(
    r"^(Required|StringLength|MaxLength|MinLength|Range|RegularExpression|EmailAddress|Phone|Compare|CreditCard|Url|Remote)\b")

# proprieta' dei metodi che raccontano "cose interessanti" per il reverse engineering
FACT_KEYS = (
    "integrations", "external_calls", "state_usage", "state_keys", "viewbag_keys", "config_keys",
    "connection_strings", "request_keys", "validations", "throws", "sql_snippets", "saves_changes",
    "returns_json", "uses_system_web", "unresolved_calls", "unresolved_redirects", "unresolved_links", "unresolved_handlers",
    "messages_sent", "dynamic_sql",
)
BRIEF_KEYS = ("file", "startLine", "loc", "complexity", "verbs", "template", "model", "unresolved")
ROUTE_KEYS = ("id", "name", "flavor", "verbs", "template", "friendly_urls", "controller", "action", "area", "authorization", "filters")
TRACE_FLAGS = ("state_usage", "uses_system_web", "saves_changes", "returns_json", "abstract")
DATA_BUCKET = {"READS": "reads", "WRITES": "writes", "CALLS_PROC": "calls"}
HEURISTIC_NOTE = "Archi con via='sql' o via='ef-tracking' sono euristici (DEDOTTO). Verifica sul sorgente prima di farne una regola."

# segreti nel codice legacy (connection string, chiavi): mascherati prima che il sorgente arrivi a un LLM
_STRING_LITERAL = re.compile(r'@?\$?"(?:[^"\\\n]|\\.|"")*"')
_SECRET_IN_STRING = re.compile(
    r"(?i)\b(password|passwd|pwd|secret|client[_ ]?secret|api[_-]?key|access[_-]?key|account[_-]?key"
    r"|shared[_-]?access[_-]?key|token)(\s*[=:]\s*)([^;\"'\s]+)")
_SECRET_ASSIGNMENT = re.compile(
    r'(?i)(\b\w*(?:password|passwd|pwd|secret|apikey|api_key|accesskey|token)\w*\s*[=:]\s*@?\$?")([^"\n]+)(")')
REDACTED = "***"


def redact_secrets(text: str) -> str:
    """Maschera password, chiavi e token nelle stringhe del codice. Non cambia il numero di righe."""
    text = _SECRET_ASSIGNMENT.sub(lambda m: m.group(1) + REDACTED + m.group(3), text)
    return _STRING_LITERAL.sub(
        lambda lit: _SECRET_IN_STRING.sub(lambda s: s.group(1) + s.group(2) + REDACTED, lit.group(0)), text)


def _read_lines(path: str) -> list[str]:
    """Sorgenti legacy: UTF-8 (con o senza BOM), UTF-16 con BOM oppure Windows-1252, frequente nei progetti .NET Framework."""
    with open(path, "rb") as f:
        raw = f.read()
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16").splitlines()
    try:
        return raw.decode("utf-8-sig").splitlines()
    except UnicodeDecodeError:
        return raw.decode("cp1252", errors="replace").splitlines()


def _load_jsonl(path: str) -> list[dict]:
    """Righe JSON valide del file; quelle rotte (es. scrittura interrotta) vengono saltate con un avviso."""
    records = []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            try:
                if line.strip():
                    records.append(json.loads(line))
            except json.JSONDecodeError:
                print(f"[warn] {path}:{lineno}: riga non valida ignorata", file=sys.stderr)
    return records


def _append_jsonl(path: str, rec: dict) -> None:
    line = json.dumps(rec, ensure_ascii=False) + "\n"
    with open(path, "a+b") as f:
        if f.seek(0, os.SEEK_END) > 0:
            f.seek(-1, os.SEEK_END)
            if f.read(1) != b"\n":
                line = "\n" + line  # ultima riga troncata: non incollarci sopra il nuovo record
        f.write(line.encode("utf-8"))


class CodeGraph:
    def __init__(self, graph_path: str, root: str | None = None, summaries_path: str | None = None, redact: bool = True):
        with open(graph_path, encoding="utf-8") as f:
            data = json.load(f)
        self.graph_path = os.path.abspath(graph_path)
        self.meta: dict[str, Any] = data.get("meta", {})
        # una radice relativa nel grafo si intende rispetto alla cartella di graph.json
        self.root = os.path.abspath(root) if root else os.path.join(os.path.dirname(self.graph_path), self.meta.get("root") or ".")
        self.g = nx.MultiDiGraph()
        for n in data["nodes"]:
            self.g.add_node(n["id"], **n)
        for e in data["edges"]:
            self.g.add_edge(e["from"], e["to"], key=e["type"], **{k: v for k, v in e.items() if k not in ("from", "to")})
        self.redact = redact
        self.summaries_path = summaries_path
        # riassunti di enrich.py: l'ultima versione di ogni nodo vince
        self.summaries: dict[str, dict] = (
            {r["id"]: r for r in _load_jsonl(summaries_path)} if summaries_path and os.path.exists(summaries_path) else {})
        self._seams: dict[str, dict] = {}
        self._by_fqn: dict[str, str] | None = None

    # ------------------------------------------------------------------ primitive

    def attrs(self, nid: str) -> dict[str, Any]:
        return self.g.nodes[nid]

    def out(self, nid: str, types: Iterable[str]) -> list[tuple[str, dict]]:
        types = set(types)
        return [(v, d) for _, v, d in self.g.out_edges(nid, data=True) if d["type"] in types]

    def inc(self, nid: str, types: Iterable[str]) -> list[tuple[str, dict]]:
        types = set(types)
        return [(u, d) for u, _, d in self.g.in_edges(nid, data=True) if d["type"] in types]

    def summary_of(self, nid: str) -> Any:
        return (self.summaries.get(nid) or {}).get("summary")

    def brief(self, nid: str) -> dict[str, Any]:
        """Rappresentazione compatta di un nodo, adatta a liste lunghe."""
        a = self.attrs(nid)
        b = {"id": nid, "kind": a.get("kind"), "name": a.get("name")}
        b.update((k, a[k]) for k in BRIEF_KEYS if a.get(k) not in (None, "", []))
        s = self.summary_of(nid)
        if isinstance(s, dict) and s.get("purpose"):
            b["purpose"] = s["purpose"]
        return b

    # ------------------------------------------------------------------ sorgente

    def source(self, nid: str, max_lines: int = 300) -> str | None:
        """Righe del nodo, numerate e con i segreti mascherati; None se il file manca o e' fuori dalla radice dei sorgenti."""
        a = self.attrs(nid)
        path = os.path.join(self.root, a.get("file") or "")
        # il percorso arriva da graph.json: non si legge nulla fuori dalla radice
        if not a.get("file") or not self._inside_root(path) or not os.path.exists(path):
            return None
        lines = _read_lines(path)
        start = max(1, int(a.get("startLine", 1)))
        end = min(len(lines), int(a.get("endLine", len(lines))), start + max_lines - 1)
        body = "\n".join(lines[start - 1:end])
        if self.redact:
            body = redact_secrets(body)
        width = len(str(end))
        return "\n".join(f"{i:>{width}}| {text}" for i, text in zip(range(start, end + 1), body.split("\n")))

    def _inside_root(self, path: str) -> bool:
        root = os.path.realpath(self.root)
        try:
            return os.path.commonpath([root, os.path.realpath(path)]) == root
        except ValueError:  # Windows: unita' diverse
            return False

    # ------------------------------------------------------------------ dossier di un caso d'uso

    def trace(self, nid: str, max_depth: int = 8, max_nodes: int = 300) -> dict[str, Any]:
        """
        Dossier as-is di una rotta (o di un metodo): metodi raggiunti, logica nel database (stored procedure chiamate e
        trigger delle tabelle scritte), tabelle lette e scritte con i loro vincoli, viste, redirect, integrazioni, stato
        implicito, configurazione, validazioni (anche dei DTO), eccezioni, messaggi e job, punto di aggancio per i test.
        E' il contesto di partenza per la specifica di un caso d'uso.
        """
        a = self.attrs(nid)
        if a.get("kind") == "Route":
            handlers = [v for v, _ in self.out(nid, ["HANDLED_BY"])]
            entry = {k: a[k] for k in ROUTE_KEYS if a.get(k) is not None}
        else:
            handlers, entry = [nid], self.brief(nid)
        reached, truncated = self._reach(handlers, max_depth, max_nodes)

        methods, db_logic, tables, procs, views, redirects, integrations = [], [], {}, {}, {}, set(), {}
        facts: dict[str, list] = {k: [] for k in ("state", "config_keys", "validations", "throws", "request_keys",
                                                  "constraints", "messages", "schedules")}
        for m, depth, via in reached:
            ma = self.attrs(m)
            kind = ma.get("kind")
            if kind == "Page":
                views[m] = self.view_info(m)
            elif kind == "Method":
                flags = [k for k in TRACE_FLAGS if ma.get(k)]
                methods.append(self.brief(m) | {"depth": depth, "via": via} | ({"flags": flags} if flags else {}))
            elif kind in DB_LOGIC_KINDS:
                db_logic.append({"id": m, "kind": kind, "dbObject": ma.get("dbObject"), "depth": depth, "via": via,
                                 "file": ma.get("file"), "startLine": ma.get("startLine"), "inSchema": bool(ma.get("inSchema")),
                                 "dynamic_sql": bool(ma.get("dynamic_sql"))})
            else:
                continue
            for t, d in self.out(m, DATA_EDGES):
                bucket = procs if d["type"] == "CALLS_PROC" else tables
                item = bucket.setdefault(t, {"name": self.attrs(t).get("name"), "reads": [], "writes": [], "calls": [], "via": set()})
                item[DATA_BUCKET[d["type"]]].append(m)
                item["via"].add(d.get("via", "?"))
            for v, d in self.out(m, ["RENDERS"]):
                views[v] = self.view_info(v, partial=bool(d.get("partial")))
            redirects.update(r for r, _ in self.out(m, ["REDIRECTS_TO"]))
            for cat in ma.get("integrations", []):
                integrations.setdefault(cat, []).append(m)
            state = ma.get("state_usage", []) + ma.get("state_keys", []) + [f"ViewBag.{x}" for x in ma.get("viewbag_keys", [])]
            facts["state"] += [{"method": m, "item": k} for k in state]
            facts["config_keys"] += [{"method": m, "key": k, "values": self.config_values(k)} for k in ma.get("config_keys", [])]
            facts["request_keys"] += [{"method": m, "key": k} for k in ma.get("request_keys", [])]
            facts["validations"] += [{"method": m, **v} for v in ma.get("validations", [])]
            facts["throws"] += [{"method": m, **t} for t in ma.get("throws", [])]
            facts["messages"] += [{"method": m, "route": r, "line": d.get("line")} for r, d in self.out(m, ["SENDS_MESSAGE"])]
            facts["schedules"] += [{"method": m, "route": r, "line": d.get("line")} for r, d in self.out(m, ["SCHEDULES"])]
        for h in handlers:
            facts["validations"] += self.input_validations(h)
        for tid, item in tables.items():
            if item["writes"]:
                facts["constraints"] += [{"table": tid, **c} for c in self.attrs(tid).get("constraints", [])
                                         if c.get("kind") in ("CHECK", "UNIQUE", "FOREIGN KEY")]
        for item in [*tables.values(), *procs.values()]:
            item["via"] = sorted(item["via"])
        return {"entry": entry, "handlers": handlers, "methods": methods, "db_logic": db_logic, "tables": tables,
                "stored_procedures": procs, "views": list(views.values()), "redirects": sorted(redirects),
                "integrations": integrations, **facts, "seam": self.route_seam(handlers, [x["id"] for x in methods]),
                "truncated": truncated, "note": HEURISTIC_NOTE}

    def _next(self, node: str) -> Iterable[tuple[str, str]]:
        """Passi del flusso: chiamate, gestori, stored procedure chiamate e trigger delle tabelle che il nodo scrive."""
        for v, d in self.out(node, FLOW_EDGES + ("CALLS_PROC",)):
            yield v, d["type"]
        for t, _ in self.out(node, ["WRITES"]):
            for trg, _ in self.out(t, ["TRIGGERS"]):
                yield trg, "TRIGGERS"

    def _reach(self, start: list[str], max_depth: int, max_nodes: int) -> tuple[list[tuple[str, int, str]], bool]:
        """Visita in ampiezza lungo il flusso di esecuzione: (nodo, profondita', arco di arrivo) e se e' stata troncata."""
        seen, order = set(start), []
        queue = deque((s, 0) for s in start)
        while queue and len(order) < max_nodes:
            node, depth = queue.popleft()
            if depth >= max_depth:
                continue
            for nxt, etype in self._next(node):
                if nxt not in seen:
                    seen.add(nxt)
                    order.append((nxt, depth + 1, etype))
                    queue.append((nxt, depth + 1))
        return [(s, 0, "entry") for s in start] + order, bool(queue) or len(order) >= max_nodes

    # ------------------------------------------------------------------ fatti di supporto al dossier

    def config_values(self, key: str) -> list[dict]:
        """Valori della chiave nei file di configurazione del repository (vuoto se mancano: vanno chiesti)."""
        cid = "CFG:" + key
        return list(self.attrs(cid).get("values") or []) if cid in self.g else []

    def type_by_fqn(self, fqn: str) -> str | None:
        if self._by_fqn is None:
            self._by_fqn = {a["fqn"]: n for n, a in self.g.nodes(data=True) if a.get("kind") == "Type" and a.get("fqn")}
        return self._by_fqn.get(fqn)

    def input_validations(self, handler: str, depth: int = 2) -> list[dict]:
        """
        Attributi di validazione ([Required], [Range], ...) dei tipi dei parametri dell'handler e delle loro proprieta':
        il model binding di MVC e Web API li applica prima dell'action, quindi fanno parte del caso d'uso.
        """
        if self.attrs(handler).get("kind") != "Method":
            return []
        found, seen = [], set()

        def visit(fqn: str, level: int, path: str) -> None:
            tid = self.type_by_fqn(re.sub(r"^(?:System\.Collections\.Generic\.)?I?(?:List|Enumerable|Collection)<(.+)>$", r"\1", fqn))
            if tid is None or tid in seen or level > depth:
                return
            seen.add(tid)
            for p in self.attrs(tid).get("properties") or []:
                for attr in p.get("attributes") or []:
                    if VALIDATION_ATTRS.match(attr):
                        found.append({"method": tid, "field": path + p["name"], "type": "DataAnnotations", "rule": attr,
                                      "line": p.get("line"), "file": p.get("file")})
                visit(str(p.get("type") or ""), level + 1, f"{path}{p['name']}.")

        for param in self.attrs(handler).get("parameters") or []:
            visit(str(param).rsplit(" ", 1)[0], 0, "")
        return found

    def test_seam(self, nid: str) -> dict[str, Any]:
        """
        Si puo' testare il metodo in processo? No se cio' che raggiunge usa stato implicito (Session, ViewState,
        HttpContext.Current, ...) o passa da pagine Web Forms: servono una richiesta HTTP o la UI.
        needs: cio' che il test deve fornire (database, chiavi di configurazione, sostituti delle integrazioni).
        """
        if nid not in self._seams:
            reached, _ = self._reach([nid], 12, 400)
            blockers, needs = [], set()
            for m, _, _ in reached:
                a = self.attrs(m)
                if a.get("kind") == "Page":
                    blockers.append({"node": m, "reason": "pagina Web Forms"})
                elif a.get("state_usage"):
                    blockers.append({"node": m, "reason": ", ".join(a["state_usage"])})
                if self.out(m, DATA_EDGES):
                    needs.add("database")
                needs.update(f"configurazione {k}" for k in a.get("config_keys") or [])
                needs.update(c for c in a.get("integrations") or [] if c in SIDE_EFFECTS)
            self._seams[nid] = {"in_process": not blockers, "blockers": blockers[:5], "needs": sorted(needs)}
        return self._seams[nid]

    def route_seam(self, handlers: list[str], methods: list[str]) -> dict[str, Any]:
        """Aggancio per i test della rotta: l'handler e, se non e' testabile in processo, i metodi piu' vicini che lo sono."""
        result: dict[str, Any] = {"handlers": [{"node": h, **self.test_seam(h)} for h in handlers if h in self.g]}
        if all(x["in_process"] for x in result["handlers"]):
            return result
        candidates = []
        for m in methods:
            a = self.attrs(m)
            interesting = (a.get("validations") or a.get("throws") or self.out(m, ["WRITES", "CALLS_PROC"])
                           or a.get("config_keys") or int(a.get("complexity") or 1) > 1)
            if m not in handlers and interesting and self.test_seam(m)["in_process"]:
                candidates.append({"node": m, **self.test_seam(m)})
        result["candidates"] = candidates[:5]
        return result

    def view_info(self, v: str, partial: bool = False) -> dict[str, Any]:
        """Vista Razor o pagina Web Forms in un dossier: modello o code-behind, campi, controlli, form e link."""
        a = self.attrs(v)
        info = {"id": v, "kind": a.get("kind"), "partial": partial or bool(a.get("partial")), "unresolved": bool(a.get("unresolved")),
                "posts_to": [r for r, _ in self.out(v, ["POSTS_TO"])], "links_to": [r for r, _ in self.out(v, ["LINKS_TO"])][:30]}
        info.update((k, a[k]) for k in ("model", "codeBehind", "pageKind") if a.get(k))
        info.update((k, a[k][:cap]) for k, cap in (("bound_fields", 60), ("controls", 80), ("roles_checked", 20)) if a.get(k))
        return info

    # ------------------------------------------------------------------ contesto e riassunti per l'LLM

    def context_pack(self, nid: str, budget_chars: int = 24000) -> str:
        """
        Testo pronto per un prompt: sorgente del metodo, fatti dal grafo e riassunti dei metodi chiamati.
        E' la "retrieval" del graph-RAG: l'LLM vede solo cio' che e' collegato, non file interi.
        """
        a = self.attrs(nid)
        parts = [f"## Nodo {nid}", f"tipo: {a.get('kind')}  file: {a.get('file')}:{a.get('startLine')}-{a.get('endLine')}"]
        parts += [f"{k}: {a[k]}" for k in ("signature", "containingType", "attributes", "doc") if a.get(k)]
        for r, _ in self.inc(nid, ["HANDLED_BY"]):
            ra = self.attrs(r)
            parts.append(f"entry point: {r}  auth: {ra.get('authorization')}  filtri: {ra.get('filters')}")
        for page, d in self.inc(nid, ["HANDLES_EVENT"]):
            parts.append(f"gestore di evento: {page}  controllo: {d.get('control')}  evento: {d.get('event')}")

        facts = {k: a[k] for k in FACT_KEYS if a.get(k)}
        facts["data_access"] = [f"{d['type']} {t} ({d.get('via')}, riga {d.get('line')})" for t, d in self.out(nid, DATA_EDGES)]
        facts["renders_views"] = [v for v, _ in self.out(nid, ["RENDERS"])]
        facts = {k: v for k, v in facts.items() if v}
        if facts:
            parts.append("### Fatti estratti staticamente\n" + json.dumps(facts, ensure_ascii=False, indent=1))
        code = self.source(nid, max_lines=400)
        if code is not None:
            parts.append("### Sorgente\n```csharp\n" + code + "\n```")

        callees = []
        for v, _ in self.out(nid, CALL_EDGES):
            # una chiamata a interfaccia vale per tutte le implementazioni
            for t in [v] + [w for w, _ in self.out(v, ["DISPATCHES_TO"])]:
                s = self.summary_of(t)
                s = s if isinstance(s, dict) else {}
                sig = self.attrs(t).get("signature")
                line = f"- {t}" + (f" -> {s['purpose']}" if s.get("purpose") else f"  [{sig}]" if sig else "  (nessun riassunto)")
                if s.get("business_rules"):
                    line += "\n    regole: " + "; ".join(r.get("rule", "") for r in s["business_rules"][:5])
                callees.append(line)
        if callees:
            parts.append("### Metodi chiamati (riassunti gia' prodotti, oppure firma)\n" + "\n".join(dict.fromkeys(callees)))
        callers = [u for u, _ in self.inc(nid, CALL_EDGES)][:10]
        if callers:
            parts.append("### Chiamato da\n" + "\n".join(f"- {c}" for c in callers))

        text = "\n\n".join(parts)
        return text if len(text) <= budget_chars else text[:budget_chars] + "\n\n[...troncato per budget...]"

    def source_hash(self, nid: str) -> str:
        code = self.source(nid, max_lines=10_000)
        return hashlib.sha256((nid if code is None else code).encode("utf-8")).hexdigest()[:16]

    def save_summary(self, nid: str, summary: dict, model: str, input_hash: str) -> None:
        rec = {"id": nid, "hash": input_hash, "model": model,
               "at": _dt.datetime.now().astimezone().isoformat(timespec="seconds"), "summary": summary}
        self.summaries[nid] = rec
        if self.summaries_path:
            _append_jsonl(self.summaries_path, rec)
