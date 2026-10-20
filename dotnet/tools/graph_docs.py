"""
Documentazione Markdown generata dal grafo: la usano un agente (Claude Code, GitHub Copilot o altri) con i normali
strumenti su file, cioe' lettura e ricerca testuale, e le persone in un editor o su un sito Markdown.

    python graph_docs.py --graph graph.json --summaries summaries.jsonl --out C:\\src\\MiaApp\\docs\\graph

Pensata per progetti grandi: tanti file piccoli, un indice per livello e gli id del grafo (M:, T:, R:, DB:, SP:, V:, P:)
sempre scritti per esteso, cosi' una ricerca testuale su un id trova ogni punto in cui il nodo compare.

    INDEX.md                      punto di partenza: numeri, progetti, entry point, dati, report
    routes/INDEX.md               gruppi di entry point (controller, cartelle Web Forms, servizi, batch, job, messaggi)
    routes/<gruppo>.md            rotte del gruppo
    routes/<gruppo>/<rotta>.md    dossier di una rotta: il contesto di partenza per un caso d'uso
    types/INDEX.md                progetti
    types/<progetto>/INDEX.md     namespace e tipi del progetto
    types/<progetto>/<tipo>.md    metodi del tipo (le classi con molti metodi continuano in <tipo>.2.md, ...)
    tables/INDEX.md               tabelle, viste, stored procedure, funzioni e trigger
    tables/<tabella>.md           colonne e vincoli dallo schema SQL, chi legge, chi scrive, da quali rotte
    tables/sp-<nome>.md, fn-, tr- stored procedure, funzioni e trigger: parametri, cosa leggono e scrivono, eccezioni
    views/INDEX.md                viste Razor e file Web Forms per progetto
    views/<progetto>.md           dettaglio di viste, pagine, controlli, master page
    reports/*.md                  autorizzazioni, proprieta' dei dati, domini candidati, database, configurazione,
                                  stato implicito, integrazioni, complessita', lacune

La cartella di uscita viene rigenerata a ogni esecuzione: si cancellano solo INDEX.md e le sottocartelle elencate
sopra, e solo se la cartella e' vuota o e' gia' stata generata da questo script (file .graph-docs).
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from collections import defaultdict
from typing import Any, Callable, Iterable

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from codegraph import CALL_EDGES, DATA_EDGES, NETWORK_FLAVORS, CodeGraph  # noqa: E402

MARKER = ".graph-docs"
SECTIONS = ("routes", "types", "tables", "views", "reports")
HOME = ("Indice generale", "INDEX.md")
CAP = 50                         # elementi massimi in un elenco: oltre si scrive "... e altri N"
ROUTE_DEPTH, ROUTE_NODES = 12, 400
SPLIT_AT, METHODS_PER_FILE = 60, 40  # le classi con piu' di 60 metodi si dividono in file da 40 metodi

FACT_LABELS = (
    ("integrations", "Integrazioni"), ("external_calls", "Chiamate esterne"), ("state_usage", "Stato implicito"),
    ("state_keys", "Chiavi di stato"), ("viewbag_keys", "ViewBag"), ("config_keys", "Chiavi di configurazione"),
    ("connection_strings", "Connection string"), ("request_keys", "Parametri della richiesta"),
    ("sql_snippets", "SQL"), ("unresolved_calls", "Chiamate non risolte"), ("unresolved_redirects", "Redirect non risolti"),
    ("messages_sent", "Messaggi inviati"),
)
FLAG_LABELS = (("saves_changes", "chiama SaveChanges"), ("returns_json", "restituisce JSON"),
               ("uses_system_web", "dipende da System.Web"),
               ("dynamic_sql", "SQL dinamico (EXEC di una stringa: tabelle e procedure non determinabili)"))
PAGE_KINDS = {"aspx": "pagina Web Forms", "ascx": "controllo utente", "master": "master page",
              "asmx": "servizio web (asmx)", "ashx": "handler (ashx)", "asax": "Global.asax", "svc": "servizio WCF (svc)"}
VIEW_LINKS = (("USES_MASTER", "Master page"), ("USES_LAYOUT", "Layout"), ("USES_CONTROL", "Controlli utente"),
              ("RENDERS_PARTIAL", "Partial"))
TRACE_FLAGS = {"state_usage": "stato implicito", "uses_system_web": "System.Web", "saves_changes": "SaveChanges",
               "returns_json": "JSON", "abstract": "astratto"}
# gruppi degli entry point che non sono action o pagine
FLAVOR_GROUPS = {"wcf": "wcf", "service": "servizio Windows", "batch": "batch", "desktop": "desktop", "job": "job",
                 "timer": "timer", "message": "messaggi", "httpmodule": "modulo HTTP", "httphandler": "handler HTTP",
                 "signalr": "hub", "owin": "owin"}
DB_LABELS = {"table": "Tabella", "view": "Vista", "procedure": "Stored procedure", "function": "Funzione", "trigger": "Trigger"}
ROUTE_DETAILS = (("schedule", "pianificazione"), ("scheduler", "scheduler"), ("message", "messaggio"),
                 ("events", "eventi dell'applicazione"), ("registeredIn", "registrato in"), ("unresolved", "attenzione"))
UNRESOLVED = (("unresolved_calls", "Chiamate non risolte (reflection, dynamic, riferimenti mancanti)"),
              ("unresolved_redirects", "Redirect non risolti"), ("unresolved_links", "Link non risolti"),
              ("unresolved_handlers", "Gestori di evento non trovati"), ("unresolved_code_behind", "Code-behind non trovato"))


# ---------------------------------------------------------------------------- testo

def slug(text: str) -> str:
    return re.sub(r"[^\w.@-]+", "_", text).strip("_.")[:120] or "_"


def anchor(nid: str) -> str:
    return "n-" + hashlib.sha1(nid.encode("utf-8")).hexdigest()[:10]


def code(x: Any) -> str:
    s = str(x).replace("\n", " ")
    return f"`` {s} ``" if "`" in s else f"`{s}`"


def cell(x: Any) -> str:
    return str(x).replace("|", "\\|").replace("\n", " ")


def label(x: Any) -> str:
    """Testo di un link o di un titolo; il '|' va protetto anche fuori dalle tabelle (le rotte GET|POST)."""
    return str(x).replace("[", "\\[").replace("]", "\\]").replace("|", "\\|")


def more(items: list, cap: int) -> str:
    return f"... e altri {len(items) - cap}" if len(items) > cap else ""


def joined(items: Iterable, fmt: Callable = str, cap: int = CAP, sep: str = ", ") -> str:
    """Elenco su una riga: i primi cap elementi, poi "... e altri N"."""
    items = list(items)
    return " ".join(x for x in (sep.join(fmt(x) for x in items[:cap]), more(items, cap)) if x)


def bullets(items: Iterable, fmt: Callable = str, cap: int = CAP, indent: str = "") -> list[str]:
    """Elenco puntato: i primi cap elementi, poi "... e altri N"."""
    items = list(items)
    return [f"{indent}- {fmt(x)}" for x in items[:cap]] + ([f"{indent}- {more(items, cap)}"] if len(items) > cap else [])


def table(*cols: str) -> list[str]:
    return [f"| {' | '.join(cols)} |", "| " + " | ".join("---" for _ in cols) + " |"]


def section(title: str, body: list[str]) -> list[str]:
    """Sezione "## titolo" seguita dal corpo; niente se il corpo e' vuoto."""
    return [f"## {title}", "", *body, ""] if body else []


def event_text(d: dict) -> str:
    control, event = d.get("control") or "", d.get("event") or "?"
    return event if not control or control.startswith("(") else f"{control}.{event}"


def route_group(a: dict) -> str:
    flavor, ctrl = a.get("flavor") or "", str(a.get("controller") or "altro")
    if flavor == "webforms":
        return f"pagine {ctrl}"
    if flavor in ("asmx", "ashx"):
        return f"{flavor} {ctrl}"
    if flavor in FLAVOR_GROUPS:
        return f"{FLAVOR_GROUPS[flavor]} {ctrl}"
    if flavor == "global":
        return "Global.asax"
    return f"{a['area']} {ctrl}" if a.get("area") else ctrl


def method_title(a: dict) -> str:
    params = [str(p).rsplit(" ", 1)[0] for p in a.get("parameters") or []]
    return f"{str(a.get('name') or '?').split('.', 1)[-1]}({', '.join(params)})"


def validation_text(v: dict) -> str:
    text = ": ".join(p for p in (f"campo {code(v['field'])}" if v.get("field") else "", cell(v.get("message") or "")) if p)
    detail = ", ".join(str(x) for x in (v.get("type"), v.get("rule")) if x)
    return ((text or "(senza messaggio)") + (f" ({cell(detail)})" if detail else "")
            + (f", riga {v['line']}" if v.get("line") else ""))


# ---------------------------------------------------------------------------- generatore

class Docs:
    def __init__(self, cg: CodeGraph, out: str):
        self.cg, self.out = cg, os.path.abspath(out)
        self.loc: dict[str, str] = {}                              # id nodo -> "percorso.md#ancora", relativo a out
        self.groups: dict[str, list[str]] = {}                     # gruppo di rotte -> rotte
        self.group_file: dict[str, str] = {}
        self.methods: dict[str, list[str]] = {}                    # tipo -> metodi in ordine di riga
        self.chunks: dict[str, list[tuple[str, list[str]]]] = {}   # tipo -> [(file, metodi)]
        self.orphans: dict[str, list[str]] = defaultdict(list)     # progetto -> metodi senza tipo
        # dei dossier si tiene solo il necessario ai report: tenerli interi su un grafo grande costa gigabyte
        self.route_tables: dict[str, dict[str, tuple[bool, bool]]] = {}  # rotta -> tabella -> (legge, scrive)
        self.reached_by: dict[str, set[str]] = defaultdict(set)          # nodo -> rotte il cui dossier lo raggiunge
        self.route_inputs: dict[str, dict[str, set[str]]] = {}           # rotta che scrive -> campo in ingresso -> dove
        self._names: dict[str, set[str]] = {}
        self.files = 0

    # ------------------------------------------------------------------ percorsi e link

    def new_file(self, folder: str, name: str) -> str:
        """Nome univoco nella cartella, anche su file system che non distinguono maiuscole e minuscole."""
        used = self._names.setdefault(folder, {"index"})
        base = slug(name)
        cand, i = base, 2
        while cand.lower() in used:
            cand, i = f"{base}-{i}", i + 1
        used.add(cand.lower())
        return f"{folder}/{cand}.md"

    def rel(self, target: str, from_file: str) -> str:
        return os.path.relpath(os.path.join(self.out, target), os.path.dirname(os.path.join(self.out, from_file))).replace(os.sep, "/")

    def link(self, nid: str, f: str, text: str | None = None) -> str:
        text = text or (str(self.cg.attrs(nid).get("name") or nid) if nid in self.cg.g else nid)
        if nid not in self.loc:
            return code(text)
        path, _, frag = self.loc[nid].partition("#")
        return f"[{label(text)}]({self.rel(path, f)}{'#' + frag if frag else ''})"

    def ref(self, nid: str, f: str, text: str | None = None) -> str:
        """Link piu' id per esteso: l'id rende il punto trovabile con una ricerca testuale."""
        return f"{self.link(nid, f, text)} {code(nid)}"

    def src(self, a: dict, f: str, line: int | None = None) -> str:
        if not a.get("file"):
            return "-"
        line = line or a.get("startLine")
        text = f"{a['file']}:{line}" if line else a["file"]
        here = os.path.dirname(os.path.join(self.out, f))
        try:
            href = os.path.relpath(os.path.join(self.cg.root, a["file"]), here).replace(os.sep, "/")
        except ValueError:  # Windows: unita' diverse
            return code(text)
        return f"[{label(text)}](<{href}{'#L' + str(line) if line else ''}>)"

    def page(self, f: str, title: str, *crumbs: tuple[str, str]) -> list[str]:
        """Titolo e riga di navigazione (le briciole indicate piu' l'indice generale)."""
        return [f"# {title}", "", " · ".join(f"[{t}]({self.rel(p, f)})" for t, p in (*crumbs, HOME)), ""]

    def emit(self, path: str, lines: list[str]) -> None:
        full = os.path.join(self.out, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(lines).rstrip() + "\n")
        self.files += 1

    def nodes(self, *kinds: str) -> list[tuple[str, dict]]:
        return sorted(((n, a) for n, a in self.cg.g.nodes(data=True) if a.get("kind") in kinds), key=lambda x: x[0])

    # ------------------------------------------------------------------ pianificazione: dove finisce ogni nodo

    def plan(self) -> None:
        cg, groups = self.cg, defaultdict(list)
        for nid, a in self.nodes("Type", "Project", "Table", "StoredProcedure", "Trigger", "Route", "Config"):
            kind = a.get("kind")
            if kind == "Type":
                self.loc[nid] = self.new_file(f"types/{slug(a.get('project') or '_')}", a.get("fqn") or nid[2:])
            elif kind == "Project":
                self.loc[nid] = f"types/{slug(a.get('name') or nid)}/INDEX.md"
            elif kind == "Route":
                groups[route_group(a)].append(nid)
            elif kind == "Config":
                self.loc[nid] = f"reports/configuration.md#{anchor(nid)}"
            else:
                prefix = {"Trigger": "tr-", "StoredProcedure": "fn-" if a.get("dbObject") == "function" else "sp-"}.get(kind, "")
                self.loc[nid] = self.new_file("tables", prefix + str(a.get("name") or nid))
        self.groups = {g: sorted(groups[g]) for g in sorted(groups)}
        for group, routes in self.groups.items():
            path = self.group_file[group] = self.new_file("routes", group)
            for rid in routes:
                self.loc[rid] = self.new_file(path[:-3], rid[2:])

        for nid, a in self.nodes("Type", "Method", "View", "Page"):
            project = slug(a.get("project") or "_")
            if a.get("kind") == "Type":
                methods = self.methods[nid] = sorted((v for v, _ in cg.out(nid, ["CONTAINS"]) if cg.attrs(v).get("kind") == "Method"),
                                                     key=lambda m: (int(cg.attrs(m).get("startLine") or 0), m))
                base = self.loc[nid]
                size = METHODS_PER_FILE
                parts = [methods] if len(methods) <= SPLIT_AT else [methods[i:i + size] for i in range(0, len(methods), size)]
                self.chunks[nid] = [(base if k == 0 else f"{base[:-3]}.{k + 1}.md", part) for k, part in enumerate(parts)]
                self.loc.update((mid, f"{path}#{anchor(mid)}") for path, part in self.chunks[nid] for mid in part)
            elif a.get("kind") == "Method":
                if not any(cg.attrs(u).get("kind") == "Type" for u, _ in cg.inc(nid, ["CONTAINS"])):
                    self.orphans[a.get("project") or "_"].append(nid)
                    self.loc[nid] = f"types/{project}/_senza-tipo.md#{anchor(nid)}"
            else:
                self.loc[nid] = f"views/{project}.md#{anchor(nid)}"

    def generate(self) -> int:
        self.plan()
        self.write_routes()   # per primo: raccoglie route_tables e reached_by, che servono a tutti gli altri
        self.write_types()
        self.write_tables()
        self.write_views()
        self.write_reports()
        self.write_index()
        return self.files

    # ------------------------------------------------------------------ blocchi comuni

    def edge_list(self, nid: str, f: str, title: str, types: Iterable[str], out: bool = True, cap: int = CAP) -> list[str]:
        edges = self.cg.out(nid, types) if out else self.cg.inc(nid, types)
        targets = sorted(dict.fromkeys(v for v, _ in edges))
        return [f"- {title} ({len(targets)}):"] + bullets(targets, lambda v: self.ref(v, f), cap, "  ") if targets else []

    def fact_lines(self, a: dict) -> list[str]:
        lines = [f"- {title}: " + joined(a[key], code, 30) for key, title in FACT_LABELS if a.get(key)]
        if a.get("validations"):
            lines += [f"- Validazioni ({len(a['validations'])}):"] + [f"  - {validation_text(v)}" for v in a["validations"]]
        if a.get("throws"):
            lines += ["- Eccezioni lanciate:"] + [f"  - {code(t.get('type'))} {cell(t.get('message') or '')} (riga {t.get('line')})"
                                                  for t in a["throws"]]
        flags = [text for key, text in FLAG_LABELS if a.get(key)]
        return lines + (["- Note: " + ", ".join(flags)] if flags else [])

    def data_lines(self, nid: str, f: str) -> list[str]:
        rows = sorted(self.cg.out(nid, DATA_EDGES), key=lambda x: (x[1]["type"], x[0]))
        lines = []
        for t, d in rows:
            extra = ", ".join(str(x) for x in (d.get("via"), f"riga {d['line']}" if d.get("line") else None) if x)
            lines.append(f"  - {d['type']} {self.ref(t, f)}" + (f" ({extra})" if extra else ""))
        return ["- Dati:"] + lines if lines else []

    def summary_lines(self, nid: str) -> list[str]:
        rec = self.cg.summaries.get(nid) or {}
        s = rec.get("summary")
        if not isinstance(s, dict) or not s.get("purpose"):
            return []
        return [f"- Riassunto ({rec.get('model')}, da validare): {cell(s['purpose'])}"] + [
            f"  - regola: {cell(r.get('rule', ''))} ({r.get('confidence', '?')}, righe {r.get('lines', '?')})"
            for r in (s.get("business_rules") or [])[:10] if isinstance(r, dict)]

    def db_label(self, a: dict) -> str:
        if a.get("kind") == "Trigger":
            return "Trigger"
        if a.get("dbObject") in DB_LABELS:
            return DB_LABELS[a["dbObject"]]
        return "Stored procedure" if a.get("kind") == "StoredProcedure" else "Tabella"

    def config_text(self, values: list[dict], f: str) -> str:
        if not values:
            return "nessun valore nei file di configurazione del repository: chiedilo"
        return "; ".join(f"{code(v.get('value'))} ({self.src(v, f, v.get('line'))}"
                         + (f", ambiente {v['environment']}" if v.get("environment") else "") + ")" for v in values)

    def seam_lines(self, seam: dict, f: str) -> list[str]:
        """Dove agganciare i test del caso d'uso: l'handler se e' testabile in processo, altrimenti i metodi che lo sono."""
        def needs(s: dict) -> str:
            return f"; serve: {', '.join(s['needs'])}" if s.get("needs") else ""
        lines = []
        for h in seam.get("handlers") or []:
            if h["in_process"]:
                lines.append(f"- {self.link(h['node'], f)}: testabile in processo{needs(h)}")
            else:
                why = ", ".join(f"{b['reason']} in {self.link(b['node'], f)}" for b in h["blockers"])
                lines.append(f"- {self.link(h['node'], f)}: non in processo ({why}): test via HTTP o UI{needs(h)}")
        for c in seam.get("candidates") or []:
            lines.append(f"  - in alternativa, in processo: {self.ref(c['node'], f)}{needs(c)}")
        return lines

    def input_fields(self, t: dict) -> dict[str, set[str]]:
        """Campi in ingresso di un caso d'uso per nome: campi delle viste e delle pagine, parametri della richiesta, proprieta' dei DTO."""
        cg, fields = self.cg, defaultdict(set)
        for v in t["views"]:
            for bf in v.get("bound_fields") or []:
                name = str(bf).split(" (")[0].split(".")[-1]
                fields[name.lower()].add(f"{cg.attrs(v['id']).get('name') or v['id']} ({bf})")
        for x in t["request_keys"]:
            fields[x["key"].split("[")[-1].rstrip("]").lower()].add(x["key"])
        for h in t["handlers"]:
            for param in cg.attrs(h).get("parameters") or [] if h in cg.g else []:
                tid = cg.type_by_fqn(str(param).rsplit(" ", 1)[0])
                for prop in (cg.attrs(tid).get("properties") or []) if tid else []:
                    fields[prop["name"].lower()].add(f"{cg.attrs(tid).get('name')}.{prop['name']}")
        return fields

    def reached_line(self, nid: str, f: str, cap: int = 15) -> list[str]:
        routes = sorted(self.reached_by.get(nid, ()))
        return [f"- Raggiunto dalle rotte ({len(routes)}): " + joined(routes, lambda r: self.link(r, f, r[2:]), cap)] if routes else []

    # ------------------------------------------------------------------ rotte

    def write_routes(self) -> None:
        cg = self.cg
        for group, routes in self.groups.items():
            gf = self.group_file[group]
            for rid in routes:
                t = cg.trace(rid, ROUTE_DEPTH, ROUTE_NODES)
                self.route_tables[rid] = {tid: (bool(v["reads"]), bool(v["writes"])) for tid, v in t["tables"].items()}
                if any(w for _, w in self.route_tables[rid].values()):
                    self.route_inputs[rid] = self.input_fields(t)
                for nid in ([m["id"] for m in t["methods"]] + [*t["tables"], *t["stored_procedures"]]
                            + [x["id"] for x in t["db_logic"]] + [v["id"] for v in t["views"]]):
                    self.reached_by[nid].add(rid)
                f = self.loc[rid]
                header = self.page(f, label(rid[2:]), (f"Gruppo {group}", gf), ("Entry point", "routes/INDEX.md"))
                self.emit(f, header + self.route_lines(rid, f, t))

            lines = self.page(gf, f"Entry point: {label(group)}", ("Entry point", "routes/INDEX.md"))
            lines += table("Rotta", "Handler", "Autorizzazione", "Tabelle scritte")
            for rid in routes:
                writes = sorted(cg.attrs(tid).get("name") or tid for tid, (_, w) in self.route_tables[rid].items() if w)
                handlers = [v for v, _ in cg.out(rid, ["HANDLED_BY"])][:3]
                auth = "; ".join(cg.attrs(rid).get("authorization") or [])
                lines.append(f"| {self.link(rid, gf, rid[2:])} | {', '.join(self.link(h, gf) for h in handlers)} "
                             f"| {cell(auth or '-')} | {cell(joined(writes, cap=5)) or '-'} |")
            self.emit(gf, lines)

        f = "routes/INDEX.md"
        lines = self.page(f, "Entry point")
        lines += ["Ogni gruppo raccoglie le rotte di un controller, di una cartella Web Forms o di un servizio, oppure gli entry "
                  "point che non passano dal browser: servizi WCF e Windows, batch, job, timer, handler di messaggi, "
                  "moduli e handler HTTP, hub SignalR, OWIN.", ""]
        lines += table("Gruppo", "Rotte", "Senza autorizzazione dichiarata")
        for g, rs in self.groups.items():
            open_ = sum(1 for r in rs if not cg.attrs(r).get("authorization"))
            lines.append(f"| [{label(g)}]({self.rel(self.group_file[g], f)}) | {len(rs)} | {open_} |")
        self.emit(f, lines)

    def route_lines(self, rid: str, f: str, t: dict) -> list[str]:
        cg, a = self.cg, self.cg.attrs(rid)
        auth = a.get("authorization") or []
        lines = [f"- Id: {code(rid)}",
                 f"- Tipo: {a.get('flavor') or '?'} · verbi {', '.join(a.get('verbs') or [])} · template {code(a.get('template'))}"]
        if a.get("friendly_urls"):
            lines.append("- URL alternativi (MapPageRoute): " + ", ".join(code(u) for u in a["friendly_urls"]))
        details = [f"{title} {code(joined(a[k]) if isinstance(a[k], list) else a[k])}" for k, title in ROUTE_DETAILS if a.get(k)]
        if details:
            lines.append("- Dettagli: " + " · ".join(details))
        if auth:
            lines.append("- Autorizzazione: " + "; ".join(code(x) for x in auth))
        elif a.get("flavor") in NETWORK_FLAVORS:
            lines.append("- Autorizzazione: nessuna dichiarata (verifica filtri globali, moduli HTTP, web.config e controlli nel codice)")
        else:
            lines.append("- Autorizzazione: non applicabile, l'entry point non e' raggiungibile da un utente via rete")
        if a.get("filters"):
            lines.append("- Filtri: " + ", ".join(code(x) for x in a["filters"]))
        lines.append("- Handler: " + ", ".join(self.ref(h, f) for h in t["handlers"]))
        spec = (cg.summaries.get(rid) or {}).get("summary")
        if isinstance(spec, dict) and spec.get("use_case"):
            lines.append(f"- Caso d'uso (LLM, da validare): {cell(spec.get('use_case'))} — {cell(spec.get('goal') or '')}")
        lines.append("")

        rows = []
        for m in t["methods"][:80]:
            note = "; ".join(x for x in (m.get("purpose"), ", ".join(TRACE_FLAGS.get(k, k) for k in m.get("flags", []))) if x)
            rows.append(f"| {m['depth']} | {self.ref(m['id'], f)} | {self.src(cg.attrs(m['id']), f)} | {cell(note) or '-'} |")
        if len(t["methods"]) > 80:
            rows.append(f"| | {more(t['methods'], 80)} (dossier troncato a {ROUTE_NODES} nodi) | | |")
        header = table("Prof.", "Metodo", "Sorgente", "Scopo e note")
        lines += section(f"Metodi raggiunti ({len(t['methods'])})", header + rows if rows else [])

        rows = []
        for tid, v in sorted(t["tables"].items()):
            origin = joined(sorted(set(v["reads"] + v["writes"])), lambda x: self.link(x, f), 4)
            reads, writes = "R" if v["reads"] else "", "W" if v["writes"] else ""
            rows.append(f"| {self.ref(tid, f)} | {reads} | {writes} | {', '.join(v['via'])} | {origin} |")
        for pid, v in sorted(t["stored_procedures"].items()):
            callers = ", ".join(self.link(x, f) for x in v["calls"][:4])
            rows.append(f"| {self.ref(pid, f)} (stored procedure) | | | {', '.join(v['via'])} | {callers} |")
        lines += section("Dati", table("Tabella", "Lettura", "Scrittura", "Via", "Da") + rows if rows else [])
        db_rows = [f"- {self.ref(x['id'], f)}: {self.db_label(cg.attrs(x['id'])).lower()}, "
                   + ("raggiunto via chiamata" if x["via"] == "CALLS_PROC" else "scatta sulle scritture di una tabella")
                   + f", {self.src(cg.attrs(x['id']), f)}" + (" · SQL dinamico" if x["dynamic_sql"] else "")
                   + ("" if x["inSchema"] else " · non trovato nello schema SQL") for x in t["db_logic"]]
        lines += section("Logica nel database", db_rows)
        lines += section("Vincoli del database sulle tabelle scritte", [
            f"- {self.link(c['table'], f)}: {c['kind']} " + code(c.get("expression") or ", ".join(c.get("columns") or []))
            + (f" verso {code(c['references'])}" if c.get("references") else "") + (f" dove {code(c['filter'])}" if c.get("filter") else "")
            + f", {self.src({'file': c.get('file')}, f, c.get('line'))}" for c in t["constraints"]])

        rows = []
        for v in t["views"]:
            desc = [x for x in (v.get("model") and f"modello {code(v['model'])}",
                                v.get("codeBehind") and f"code-behind {code(v['codeBehind'])}",
                                v.get("unresolved") and "file non trovato") if x]
            rows.append(f"- {self.ref(v['id'], f)}" + (f" ({', '.join(desc)})" if desc else ""))
            if v.get("bound_fields"):
                rows.append("  - campi: " + ", ".join(code(x) for x in v["bound_fields"][:30]))
            if v.get("posts_to"):
                rows.append("  - form verso: " + ", ".join(self.link(r, f, r[2:]) for r in v["posts_to"]))
            if v.get("links_to"):
                rows.append("  - link verso: " + ", ".join(self.link(r, f, r[2:]) for r in v["links_to"]))
        lines += section("Viste e pagine", rows)
        lines += section("Redirect", [f"- {self.ref(r, f)}" for r in t["redirects"]])
        lines += section("Integrazioni", [f"- {cat}: " + ", ".join(self.link(m, f) for m in sorted(set(ms)))
                                          for cat, ms in sorted(t["integrations"].items())])
        # "ViewState" e' superfluo se lo stesso metodo ha gia' una chiave ViewState[...]
        keyed = {(x["method"], x["item"].split("[")[0]) for x in t["state"] if "[" in x["item"]}
        state = [("stato", x["item"], x["method"]) for x in t["state"] if "[" in x["item"] or (x["method"], x["item"]) not in keyed]
        state += [("richiesta", x["key"], x["method"]) for x in t["request_keys"]]
        config = [f"- config: {code(x['key'])} = {self.config_text(x['values'], f)} in {self.link(x['method'], f)}"
                  for x in t["config_keys"]]
        lines += section("Stato implicito, configurazione, parametri",
                         [f"- {kind}: {code(item)} in {self.link(m, f)}" for kind, item, m in state] + config)
        lines += section("Validazioni", [f"- {validation_text(v)} in {self.link(v['method'], f)}" for v in t["validations"]])
        lines += section("Eccezioni", [f"- {code(x.get('type'))} {cell(x.get('message') or '')}"
                                       + (f" se {code(x['condition'])}" if x.get("condition") else "")
                                       + f" in {self.link(x['method'], f)}, riga {x.get('line')}" for x in t["throws"]])
        lines += section("Messaggi e job", [f"- {self.link(x['method'], f)} invia a {self.ref(x['route'], f)}" for x in t["messages"]]
                         + [f"- {self.link(x['method'], f)} pianifica {self.ref(x['route'], f)}" for x in t["schedules"]])
        lines += section("Aggancio per i test", self.seam_lines(t["seam"], f))
        if t["truncated"]:
            lines += [f"> Dossier troncato (profondita' {ROUTE_DEPTH}, {ROUTE_NODES} nodi): "
                      "segui i metodi piu' profondi dalle loro schede.", ""]
        return lines

    # ------------------------------------------------------------------ tipi e metodi

    def write_types(self) -> None:
        cg = self.cg
        by_project: dict[str, list[str]] = defaultdict(list)
        for nid, a in self.nodes("Type"):
            by_project[a.get("project") or "_"].append(nid)
        projects = {a.get("name"): nid for nid, a in self.nodes("Project")}
        names = sorted(set(by_project) | set(projects))

        f = "types/INDEX.md"
        lines = self.page(f, "Progetti e tipi") + table("Progetto", "Tipi", "Metodi", "Errori di dichiarazione")
        for project in names:
            types = by_project.get(project, [])
            errors = cg.attrs(projects[project]).get("declarationErrors", "-") if project in projects else "-"
            methods = sum(len(self.methods[t]) for t in types)
            lines.append(f"| [{label(project)}]({slug(project)}/INDEX.md) | {len(types)} | {methods} | {errors} |")
        self.emit(f, lines)

        for project in names:
            self.write_project_index(project, by_project.get(project, []), projects.get(project))
            for tid in by_project.get(project, []):
                self.write_type(tid)
        for project, methods in self.orphans.items():
            path = f"types/{slug(project)}/_senza-tipo.md"
            lines = self.page(path, "Metodi senza tipo nel grafo", ("Progetto", f"types/{slug(project)}/INDEX.md"))
            self.emit(path, lines + [x for mid in methods for x in self.method_lines(mid, path)])

    def write_project_index(self, project: str, types: list[str], pid: str | None) -> None:
        cg, f = self.cg, f"types/{slug(project)}/INDEX.md"
        lines = self.page(f, f"Progetto {label(project)}", ("Progetti", "types/INDEX.md"))
        if pid:
            a = cg.attrs(pid)
            refs = [v for v, _ in cg.out(pid, ["REFERENCES_PROJECT"])]
            lines += [f"- Id: {code(pid)}", f"- File: {self.src(a, f)}",
                      f"- Errori di dichiarazione: {a.get('declarationErrors', 0)} "
                      "(molti errori = riferimenti non ripristinati, archi persi)"]
            lines += (["- Riferimenti a progetti: " + ", ".join(self.link(r, f) for r in refs)] if refs else []) + [""]
        by_ns: dict[str, list[str]] = defaultdict(list)
        for tid in types:
            by_ns[cg.attrs(tid).get("namespace") or "(globale)"].append(tid)
        for ns in sorted(by_ns):
            lines += [f"## {label(ns)}", ""] + table("Tipo", "Genere", "Metodi", "Complessita' max", "Ruolo")
            for tid in sorted(by_ns[ns], key=lambda x: str(cg.attrs(x).get("name"))):
                a, methods = cg.attrs(tid), self.methods[tid]
                top = max((int(cg.attrs(m).get("complexity") or 0) for m in methods), default=0)
                roles = (("controller", a.get("isController")), ("DbContext", a.get("isDbContext")),
                         ("code-behind", cg.inc(tid, ["CODE_BEHIND"])), ("generato", a.get("generated")))
                role = ", ".join(x for x, on in roles if on)
                lines.append(f"| {self.link(tid, f)} | {a.get('typeKind') or '-'} | {len(methods)} | {top or '-'} | {role or '-'} |")
            lines.append("")
        self.emit(f, lines)

    def write_type(self, tid: str) -> None:
        cg, a = self.cg, self.cg.attrs(tid)
        f, chunks, methods = self.loc[tid], self.chunks[tid], self.methods[tid]
        project_index = ("Progetto", f"types/{slug(a.get('project') or '_')}/INDEX.md")
        lines = self.page(f, label(a.get("name")), project_index) + [
            f"- Id: {code(tid)}", f"- Nome completo: {code(a.get('fqn') or tid[2:])}",
            f"- Progetto: {label(a.get('project') or '_')} · {a.get('typeKind') or '?'} · {a.get('accessibility') or '?'}"
            + "".join(f" · {k}" for k in ("abstract", "static", "generated") if a.get(k)),
            f"- Sorgente: {self.src(a, f)}"]
        if len(a.get("files") or []) > 1:
            lines.append("- File (classe partial): " + ", ".join(code(x) for x in a["files"]))
        if a.get("baseType"):
            lines.append(f"- Base: {code(a['baseType'])}")
        for key, title in (("interfaces", "Interfacce"), ("attributes", "Attributi")):
            if a.get(key):
                lines.append(f"- {title}: " + ", ".join(code(x) for x in a[key]))
        if a.get("doc"):
            lines.append(f"- Doc: {cell(a['doc'])}")
        for title, etype, out in (("Rotte esposte", "EXPOSES", True), ("Tabelle dichiarate", "DECLARES_TABLE", True),
                                  ("Code-behind di", "CODE_BEHIND", False), ("Derivato da", "INHERITS", False),
                                  ("Implementato da", "IMPLEMENTS", False), ("Usato come modello dalle viste", "USES_MODEL", False)):
            lines += self.edge_list(tid, f, title, [etype], out=out)
        nested = [v for v, _ in cg.out(tid, ["CONTAINS"]) if cg.attrs(v).get("kind") == "Type"]
        if nested:
            lines.append("- Tipi annidati: " + ", ".join(self.link(v, f) for v in nested))
        props = a.get("properties") or []
        if props:
            lines += ["", f"## Proprieta' ({len(props)})", ""] + table("Proprieta'", "Tipo", "Attributi", "Riga") + [
                f"| {code(x['name'])} | {code(x.get('type'))} | {cell(', '.join(x.get('attributes') or [])) or '-'} "
                f"| {self.src(x, f, x.get('line')) if x.get('file') else x.get('line') or '-'} |" for x in props[:80]]
            if len(props) > 80:
                lines.append(f"| {more(props, 80)} | | | |")
        lines += ["", f"## Metodi ({len(methods)})", ""]
        if len(chunks) > 1:
            lines += [f"Classe grande: i metodi sono divisi in {len(chunks)} file da {METHODS_PER_FILE}: "
                      + ", ".join(f"[parte {k}]({self.rel(path, f)})" for k, (path, _) in enumerate(chunks, 1)) + ".", ""]
        if methods:
            lines += table("Metodo", "Riga", "Complessita'", "Scopo")
            for mid in methods:
                ma, s = cg.attrs(mid), cg.summary_of(mid)
                purpose = s.get("purpose") if isinstance(s, dict) else ""
                lines.append(f"| {self.link(mid, f, method_title(ma))} | {ma.get('startLine', '-')} "
                             f"| {ma.get('complexity', '-')} | {cell(purpose or '-')} |")
            lines.append("")
        self.emit(f, lines + [x for mid in chunks[0][1] for x in self.method_lines(mid, f)])
        for k, (path, part) in enumerate(chunks[1:], 2):
            title = f"{label(a.get('name'))}: metodi, parte {k} di {len(chunks)}"
            lines = self.page(path, title, (f"Tipo {a.get('name')}", f), project_index)
            self.emit(path, lines + [x for mid in part for x in self.method_lines(mid, path)])

    def method_lines(self, mid: str, f: str) -> list[str]:
        cg, a = self.cg, self.cg.attrs(mid)
        lines = [f'<a id="{anchor(mid)}"></a>', "", f"### {label(method_title(a))}", "", f"- Id: {code(mid)}"]
        if a.get("signature"):
            lines.append(f"- Firma: {code(a['signature'])}")
        details = [x for x in (a.get("endLine") and f"righe {a.get('startLine')}-{a.get('endLine')}",
                               a.get("complexity") and f"complessita' {a['complexity']}") if x]
        lines.append(f"- Sorgente: {self.src(a, f)}" + (f" ({', '.join(details)})" if details else ""))
        props = [k for k in ("static", "async", "virtual", "override", "abstract", "generated") if a.get(k)]
        if props:
            lines.append("- Proprieta': " + ", ".join(props))
        if a.get("attributes"):
            lines.append("- Attributi: " + ", ".join(code(x) for x in a["attributes"]))
        if a.get("doc"):
            lines.append(f"- Doc: {cell(a['doc'])}")
        entry = [self.link(r, f, r[2:]) for r, _ in cg.inc(mid, ["HANDLED_BY"])]
        entry += [f"{self.link(p, f)} (evento {event_text(d)})" for p, d in cg.inc(mid, ["HANDLES_EVENT"])]
        if entry:
            lines.append("- Entry point: " + ", ".join(entry))
        lines += self.reached_line(mid, f) + self.summary_lines(mid) + self.fact_lines(a) + self.data_lines(mid, f)
        for title, types in (("Viste", ["RENDERS"]), ("Redirect verso", ["REDIRECTS_TO"]), ("Chiama", ["CALLS"]),
                             ("Dispatch verso le implementazioni", ["DISPATCHES_TO"]), ("Pianifica", ["SCHEDULES"]),
                             ("Invia messaggi a", ["SENDS_MESSAGE"]), ("Configurazione", ["USES_CONFIG"])):
            lines += self.edge_list(mid, f, title, types)
        return lines + self.edge_list(mid, f, "Chiamato da", CALL_EDGES, out=False, cap=30) + [""]

    # ------------------------------------------------------------------ tabelle

    def write_tables(self) -> None:
        cg, f = self.cg, "tables/INDEX.md"
        items = self.nodes("Table", "StoredProcedure", "Trigger")
        schema = cg.meta.get("sqlSchema")
        intro = ("Oggetti dallo schema SQL e dal codice. Letture e scritture sono quelle dirette di metodi, pagine e oggetti "
                 "del database; le rotte sono quelle il cui dossier arriva all'oggetto."
                 if schema is not None else
                 "Schema SQL non fornito: tabelle e procedure vengono solo dal codice, senza colonne, vincoli e trigger.")
        lines = self.page(f, "Database: tabelle, viste, stored procedure, funzioni e trigger") + [intro, ""]
        lines += table("Nome", "Tipo", "Nello schema", "Usata dal codice", "Letture", "Scritture", "Chi scrive", "Rotte")
        for tid, a in items:
            writers = {u for u, _ in cg.inc(tid, ["WRITES"])}
            who = sorted({str(cg.attrs(u).get("project") or "database") for u in writers})
            readers = {u for u, _ in cg.inc(tid, ["READS", "CALLS_PROC"])}
            lines.append(f"| {self.link(tid, f)} | {self.db_label(a).lower()} | {'si' if a.get('inSchema') else 'no'} "
                         f"| {a.get('source') or '-'} | {len(readers)} | {len(writers)} | {cell(', '.join(who)) or '-'} "
                         f"| {len(self.reached_by.get(tid, ()))} |")
        self.emit(f, lines)
        for tid, _ in items:
            self.write_table(tid)

    def write_table(self, tid: str) -> None:
        cg, a = self.cg, self.cg.attrs(tid)
        f, kind = self.loc[tid], a.get("kind")
        lines = self.page(f, f"{self.db_label(a)} {label(a.get('name') or tid)}", ("Database", "tables/INDEX.md")) + [f"- Id: {code(tid)}"]
        if a.get("inSchema"):
            lines.append(f"- Definizione: {self.src(a, f)} (schema SQL)")
        elif cg.meta.get("sqlSchema") is not None:
            lines.append("- Definizione: non trovata nello schema SQL fornito (altro database, oggetto creato a runtime o schema incompleto)")
        if a.get("source"):
            lines.append(f"- Usata dal codice: {a['source']} (ef = Entity Framework, sql = testo SQL nel codice o nel markup)")
        if a.get("entity"):
            lines.append(f"- Entita': {code(a['entity'])}")
        if kind == "Trigger":
            lines.append(f"- Scatta: {a.get('timing') or 'AFTER'} {', '.join(a.get('events') or [])} su {code(a.get('table'))}")
        if a.get("parameters"):
            lines.append("- Parametri: " + ", ".join(code(x) for x in a["parameters"]))
        flags = [x for x in (a.get("complexity") and f"complessita' {a['complexity']}",
                             a.get("dynamic_sql") and "SQL dinamico: tabelle e procedure eseguite da stringhe non determinabili") if x]
        if flags:
            lines.append("- Note: " + ", ".join(flags))
        lines += self.edge_list(tid, f, "Mappata sul tipo", ["MAPPED_TO"])
        lines += self.edge_list(tid, f, "Dichiarata nel contesto", ["DECLARES_TABLE"], out=False) + [""]

        if kind == "Table":
            lines += self.column_lines(tid, f)
            cons = [c for c in a.get("constraints") or [] if c.get("kind") != "PRIMARY KEY"]
            lines += section(f"Vincoli ({len(cons)})", [
                f"- {c['kind']}" + (f" {code(c['name'])}" if c.get("name") else "")
                + (f" su {', '.join(code(x) for x in c.get('columns') or [])}" if c.get("columns") else "")
                + (f": {code(c['expression'])}" if c.get("expression") else "")
                + (f" verso {code(c['references'])}" if c.get("references") else "")
                + (f", ON DELETE {c['onDelete']}" if c.get("onDelete") else "")
                + (f" dove {code(c['filter'])}" if c.get("filter") else "")
                + f" ({self.src({'file': c.get('file')}, f, c.get('line'))})" for c in cons])
            refs = [f"- verso {self.ref(v, f)} ({', '.join(d.get('columns') or [])})" for v, d in cg.out(tid, ["REFERENCES"])]
            refs += [f"- da {self.ref(u, f)} ({', '.join(d.get('columns') or [])})" for u, d in cg.inc(tid, ["REFERENCES"])]
            lines += section("Chiavi esterne", refs)
            lines += section("Trigger", [f"- {self.ref(v, f)}" for v, _ in cg.out(tid, ["TRIGGERS"])])
        else:
            body = sorted(cg.out(tid, DATA_EDGES), key=lambda x: (x[1]["type"], x[0]))
            lines += section("Cosa fa", [f"- {d['type']} {self.ref(v, f)} ({self.src(a, f, d.get('line'))})" for v, d in body])
            lines += section("Eccezioni", [f"- {code(x.get('type'))} {cell(x.get('message') or '')}"
                                           + (f" se {code(x['condition'])}" if x.get("condition") else "")
                                           + f" ({self.src(a, f, x.get('line'))})" for x in a.get("throws") or []])
        for title, etype in (("Scritture", "WRITES"), ("Letture", "READS"), ("Chiamate", "CALLS_PROC")):
            rows = sorted(cg.inc(tid, [etype]), key=lambda x: x[0])
            body = [f"| {self.ref(u, f)} | {cg.attrs(u).get('project') or 'database'} | {d.get('via') or '-'} "
                    f"| {self.src(cg.attrs(u), f, d.get('line'))} |" for u, d in rows]
            lines += section(f"{title} da codice e database ({len(rows)})", table("Da", "Progetto", "Via", "Riga") + body if rows else [])
        routes = sorted(self.reached_by.get(tid, ()))
        lines += section(f"Rotte che la raggiungono ({len(routes)})", bullets(routes, lambda r: self.ref(r, f), 100))
        self.emit(f, lines)

    def column_lines(self, tid: str, f: str) -> list[str]:
        """Dizionario dati: colonne dello schema, proprieta' delle entita' mappate e campi in ingresso con lo stesso nome."""
        cg, a = self.cg, self.cg.attrs(tid)
        columns = a.get("columns") or []
        if not columns:
            return []
        props: dict[str, list[str]] = defaultdict(list)
        for ent, _ in cg.out(tid, ["MAPPED_TO"]):
            for p in cg.attrs(ent).get("properties") or []:
                mapped = next((m.group(1) for x in p.get("attributes") or [] for m in [re.match(r'Column\(\s*"([^"]+)"', x)] if m), None)
                attrs = [x for x in p.get("attributes") or [] if not x.startswith("Column")]
                props[(mapped or p["name"]).lower()].append(f"{cg.attrs(ent).get('name')}.{p['name']}" + (f" [{', '.join(attrs)}]" if attrs else ""))
        inputs: dict[str, set[str]] = defaultdict(set)
        for rid, tables in self.route_tables.items():
            if tables.get(tid, (False, False))[1]:
                for name, where in self.route_inputs.get(rid, {}).items():
                    inputs[name] |= where
        cons: dict[str, list[str]] = defaultdict(list)
        for c in a.get("constraints") or []:
            short = {"PRIMARY KEY": "PK", "FOREIGN KEY": f"FK {c.get('references')}"}.get(c["kind"], c["kind"])
            for col in c.get("columns") or []:
                cons[col.lower()].append(short)
        rows = []
        for col in columns:
            key = col["name"].lower()
            value = " ".join(x for x in (col.get("identity") and "identity", col.get("default") and f"default {col['default']}",
                                         col.get("computed") and f"calcolata {col['computed']}") if x)
            rows.append(f"| {code(col['name'])} | {code(col.get('type'))} | {'si' if col.get('nullable') else 'no'} | {cell(value) or '-'} "
                        f"| {cell(', '.join(cons.get(key, []))) or '-'} | {cell(', '.join(props.get(key, []))) or '-'} "
                        f"| {cell(joined(sorted(inputs.get(key, [])), cap=4)) or '-'} |")
        return section(f"Colonne ({len(columns)})", [
            "Proprieta' e campi in ingresso corrispondono alla colonna per nome (o per [Column]): e' un'euristica, "
            "il percorso reale del valore va verificato nel codice.", ""]
            + table("Colonna", "Tipo", "Null", "Valore", "Vincoli", "Proprieta' dell'entita'", "Campi in ingresso (per nome)") + rows)

    # ------------------------------------------------------------------ viste e pagine

    def write_views(self) -> None:
        cg = self.cg
        by_project: dict[str, list[str]] = defaultdict(list)
        for nid, a in self.nodes("View", "Page"):
            by_project[a.get("project") or "_"].append(nid)
        f = "views/INDEX.md"
        lines = self.page(f, "Viste e file Web Forms")
        lines += table("Progetto", "Viste Razor", "Pagine aspx", "Controlli e master", "Servizi e handler", "Non trovate")
        for project in sorted(by_project):
            items = [cg.attrs(n) for n in by_project[project]]
            count = [sum(1 for x in items if pred(x)) for pred in (
                lambda x: x.get("kind") == "View" and not x.get("unresolved"), lambda x: x.get("pageKind") == "aspx",
                lambda x: x.get("pageKind") in ("ascx", "master"), lambda x: x.get("pageKind") in ("asmx", "ashx", "asax"),
                lambda x: x.get("unresolved"))]
            lines.append(f"| [{label(project)}]({slug(project)}.md) | " + " | ".join(map(str, count)) + " |")
        self.emit(f, lines)

        for project, nodes in by_project.items():
            path = f"views/{slug(project)}.md"
            nodes = sorted(nodes, key=lambda n: (str(cg.attrs(n).get("file") or "~" + n), n))
            lines = self.page(path, f"Viste e pagine: {label(project)}", ("Viste", "views/INDEX.md"))
            lines += [f"- {self.link(n, path, cg.attrs(n).get('file') or n)}" for n in nodes] + [""]
            self.emit(path, lines + [x for n in nodes for x in self.view_lines(n, path)])

    def view_lines(self, vid: str, f: str) -> list[str]:
        cg, a = self.cg, self.cg.attrs(vid)
        kind = "vista Razor" if a.get("kind") == "View" else PAGE_KINDS.get(a.get("pageKind"), "file Web Forms")
        lines = [f'<a id="{anchor(vid)}"></a>', "", f"## {label(a.get('file') or vid)}", "", f"- Id: {code(vid)}",
                 f"- Tipo: {kind}" + (" (file non trovato: segnaposto creato dall'estrattore)" if a.get("unresolved") else ""),
                 f"- Sorgente: {self.src(a, f)}"]
        if a.get("model"):
            lines.append(f"- Modello: {code(a['model'])}")
        lines += self.edge_list(vid, f, "Code-behind", ["CODE_BEHIND"])
        lines += self.edge_list(vid, f, "Rotte", ["EXPOSES"])
        lines += self.edge_list(vid, f, "Renderizzata da", ["RENDERS"], out=False)
        for etype, title in VIEW_LINKS:
            lines += self.edge_list(vid, f, title, [etype])
        lines += self.edge_list(vid, f, "Usata da", [e for e, _ in VIEW_LINKS], out=False)
        events = sorted(cg.out(vid, ["HANDLES_EVENT"]), key=lambda x: x[0])
        if events:
            lines += [f"- Gestori di evento ({len(events)}):"] + [f"  - {event_text(d)} -> {self.ref(m, f)}" for m, d in events]
        for key, title in (("controls", "Controlli"), ("bound_fields", "Campi legati")):
            if a.get(key):
                lines.append(f"- {title}: " + joined(a[key], code, 60))
        lines += self.fact_lines(a) + self.data_lines(vid, f)
        for title, etype in (("Chiama (origini dati)", "CALLS"), ("Form verso", "POSTS_TO"), ("Link verso", "LINKS_TO"),
                             ("Redirect verso", "REDIRECTS_TO"), ("Child action", "RENDERS_ACTION")):
            lines += self.edge_list(vid, f, title, [etype])
        lines += [f"- {title}: " + ", ".join(code(x) for x in a[key]) for key, title in (
            ("roles_checked", "Ruoli controllati"), ("ajax_urls", "URL ajax"),
            ("unresolved_links", "Link non risolti"), ("unresolved_handlers", "Gestori non trovati")) if a.get(key)]
        if a.get("unresolved_code_behind"):
            lines.append(f"- Code-behind non trovato: {code(a['unresolved_code_behind'])}")
        stats = [f"{k} {a[k]}" for k in ("loc", "conditionals", "scripts", "forms", "inline_code_blocks") if a.get(k)]
        if stats:
            lines.append("- Misure: " + ", ".join(stats))
        return lines + self.reached_line(vid, f) + [""]

    # ------------------------------------------------------------------ report

    def write_reports(self) -> None:
        self.report_authorization()
        self.report_data_ownership()
        self.report_domains()
        self.report_database()
        self.report_configuration()
        self.report_state()
        self.report_integrations()
        self.report_hotspots()
        self.report_gaps()

    def report(self, f: str, title: str, intro: str, lines: list[str]) -> None:
        self.emit(f, self.page(f, title) + [intro, ""] + lines)

    def report_authorization(self) -> None:
        cg, f = self.cg, "reports/authorization.md"
        unprotected = {r for rs in self.groups.values() for r in rs
                       if not cg.attrs(r).get("authorization") and cg.attrs(r).get("flavor") in NETWORK_FLAVORS}
        lines = ["## Per gruppo", "", "Il dettaglio di ogni rotta e' nell'indice del suo gruppo.", ""]
        lines += table("Gruppo", "Rotte", "Senza autorizzazione", "Regole usate")
        for group, routes in self.groups.items():
            rules = sorted({x for r in routes for x in cg.attrs(r).get("authorization") or []})
            lines.append(f"| [{label(group)}]({self.rel(self.group_file[group], f)}) | {len(routes)} "
                         f"| {sum(1 for r in routes if r in unprotected)} | {cell(joined(rules, cap=5, sep='; ')) or '-'} |")
        lines += ["", f"## Rotte senza autorizzazione dichiarata ({len(unprotected)})", ""]
        lines += [f"- {self.ref(r, f)}" for r in sorted(unprotected)]
        self.report(f, "Autorizzazioni per rotta",
                    "Fonti lette: attributi [Authorize] e [AllowAnonymous] (anche ereditati da controller e metodi base, "
                    "anche sugli hub SignalR), [PrincipalPermission] e, per Web Forms e servizi .svc, le regole "
                    "`<authorization>` dei web.config. Non lette: filtri globali (FilterConfig), controlli nel codice "
                    "(User.IsInRole, verifiche manuali), anche dentro i moduli HTTP (che compaiono tra gli entry point). "
                    "Batch, job, servizi Windows, timer e handler di messaggi non sono contati: non arrivano dalla rete.", lines)

    def report_data_ownership(self) -> None:
        cg, f = self.cg, "reports/data-ownership.md"
        writers: dict[str, set[str]] = defaultdict(set)
        readers: dict[str, set[str]] = defaultdict(set)
        for group, routes in self.groups.items():
            for r in routes:
                for tid, (_, writes) in self.route_tables[r].items():
                    (writers if writes else readers)[tid].add(group)

        def some(items: Iterable[str]) -> str:
            items = sorted(items)
            return (f"{len(items)}: " if len(items) > 1 else "") + joined(items, cap=12)

        lines = table("Tabella", "Gruppi che scrivono", "Gruppi che solo leggono", "Progetti che scrivono")
        for tid in sorted(writers, key=lambda t: (-len(writers[t]), t)):
            projects = {str(cg.attrs(u).get("project") or "?") for u, _ in cg.inc(tid, ["WRITES"])}
            lines.append(f"| {self.link(tid, f)} | {cell(some(writers[tid]))} | {cell(some(readers[tid] - writers[tid])) or '-'} "
                         f"| {cell(some(projects)) or '-'} |")
        only_read = sorted(set(readers) - set(writers))
        if only_read:
            lines += ["", f"## Tabelle solo lette dalle rotte ({len(only_read)})", ""] + [f"- {self.link(t, f)}" for t in only_read]
        self.report(f, "Proprieta' dei dati",
                    "Quali gruppi di entry point scrivono e leggono ogni tabella, aggregando il dossier di ogni rotta. "
                    "Una tabella scritta da piu' gruppi e' un confine da decidere prima di dividere in servizi.", lines)

    def report_domains(self) -> None:
        """Domini candidati: comunita' (Louvain) sul grafo gruppi di entry point - tabelle, scritture pesate piu' delle letture."""
        from networkx import Graph
        from networkx.algorithms.community import louvain_communities
        f, g = "reports/domains.md", Graph()
        for group, routes in self.groups.items():
            g.add_node(("g", group))
            for r in routes:
                for tid, (_, writes) in self.route_tables[r].items():
                    old = g.get_edge_data(("g", group), ("t", tid), {}).get("weight", 0)
                    g.add_edge(("g", group), ("t", tid), weight=max(old, 3 if writes else 1))
        isolated = sorted(n[1] for n in g.nodes if n[0] == "g" and g.degree(n) == 0)
        g.remove_nodes_from([("g", x) for x in isolated])
        communities = [c for c in louvain_communities(g, weight="weight", seed=42) if any(n[0] == "g" for n in c)] if g.number_of_edges() else []
        communities.sort(key=lambda c: (-len(c), sorted(c)))
        home = {n: i for i, c in enumerate(communities) for n in c}
        writers: dict[str, set[int]] = defaultdict(set)
        for node in (n for n in g.nodes if n[0] == "t"):
            writers[node[1]] |= {home[nb] for nb in g.neighbors(node) if g[node][nb]["weight"] == 3}
        lines = []
        for i, com in enumerate(communities):
            groups = sorted(n[1] for n in com if n[0] == "g")
            tables = sorted(n[1] for n in com if n[0] == "t")
            own = [x for x in tables if writers.get(x) == {i}]
            shared = [x for x in tables if len(writers.get(x, ())) > 1]
            read_elsewhere = sorted({n[1] for gr in groups for n in g.neighbors(("g", gr)) if home[n] != i})
            lines += [f"## Candidato {i + 1}", "",
                      "- Entry point: " + joined(groups, lambda x: f"[{label(x)}]({self.rel(self.group_file[x], f)})", 30),
                      "- Tabelle scritte solo da qui: " + (joined(own, lambda x: self.link(x, f), 30) or "-"),
                      "- Tabelle condivise con altri candidati (scritte da piu' parti): " + (joined(shared, lambda x: self.link(x, f), 30) or "-"),
                      "- Tabelle di altri candidati usate da qui: " + (joined(read_elsewhere, lambda x: self.link(x, f), 30) or "-"), ""]
        if isolated:
            lines += [f"## Entry point senza dati ({len(isolated)})", "", "Non leggono ne' scrivono tabelle: vanno assegnati a mano.", "",
                      *bullets(isolated, lambda x: f"[{label(x)}]({self.rel(self.group_file[x], f)})", 100)]
        self.report(f, "Domini candidati",
                    "Raggruppamento automatico dei gruppi di entry point in base ai dati che usano (algoritmo di Louvain sul grafo "
                    "gruppi-tabelle; una scrittura pesa 3, una lettura 1). E' un punto di partenza per la capability map, "
                    "non una decisione: i nomi dei domini e i confini li decidono le persone. Le tabelle condivise sono i "
                    "punti di accoppiamento da risolvere prima di separare i servizi.", lines)

    def report_database(self) -> None:
        f, schema = "reports/database.md", self.cg.meta.get("sqlSchema")
        if schema is None:
            self.report(f, "Database", "Schema SQL non fornito: rigenera il grafo con --sql. Senza schema mancano colonne, "
                                       "vincoli, trigger e la logica delle stored procedure.", [])
            return
        objects = self.nodes("Table", "StoredProcedure", "Trigger")
        counts: dict[str, int] = defaultdict(int)
        for _, a in objects:
            if a.get("inSchema"):
                counts[self.db_label(a)] += 1
        lines = section("Contenuto dello schema", table("Tipo", "Quanti") + [f"| {k} | {v} |" for k, v in sorted(counts.items())]
                        + ["", f"File letti: {schema.get('fileCount', 0)}."])
        logic = [(n, a) for n, a in objects if a.get("kind") in ("StoredProcedure", "Trigger") and a.get("inSchema")]
        logic.sort(key=lambda x: (-len(x[1].get("throws") or []), -int(x[1].get("complexity") or 1), x[0]))
        lines += section("Logica nel database", table("Oggetto", "Tipo", "Eccezioni", "Complessita'", "Righe", "Raggiunto da rotte") + [
            f"| {self.link(n, f)} | {self.db_label(a).lower()} | {len(a.get('throws') or [])} | {a.get('complexity', '-')} "
            f"| {a.get('loc', '-')} | {len(self.reached_by.get(n, ()))} |" for n, a in logic])
        dynamic = [n for n, a in objects if a.get("dynamic_sql")]
        lines += section(f"SQL dinamico ({len(dynamic)})", ["Eseguono stringhe SQL: cosa leggono e scrivono va letto a mano.", ""]
                         + [f"- {self.ref(n, f)}" for n in dynamic] if dynamic else [])
        unused = [n for n, a in objects if a.get("inSchema") and not self.reached_by.get(n)]
        lines += section(f"Oggetti dello schema non raggiunti da nessun entry point ({len(unused)})", [
            "Possono servire ad altri sistemi, a job di SQL Server Agent, a report, o essere inutilizzati: chiedilo prima di "
            "escluderli dalla migrazione.", ""] + [f"- {self.ref(n, f)}" for n in unused] if unused else [])
        self.report(f, "Database", "Cosa contiene lo schema SQL fornito e quanta logica vive nel database.", lines)

    def report_configuration(self) -> None:
        cg, f = self.cg, "reports/configuration.md"
        lines = []
        for nid, a in self.nodes("Config"):
            values = a.get("values") or []
            notes = []
            if a.get("missing") or not values:
                notes.append("nessun valore nei file del repository: chiedi il valore di produzione")
            elif not any(v.get("environment") for v in values):
                notes.append("solo il valore base: chiedi se in produzione cambia")
            if a.get("secret"):
                notes.append("valore mascherato perche' sembra un segreto")
            users = sorted(cg.inc(nid, ["USES_CONFIG"]), key=lambda x: x[0])
            lines += [f'<a id="{anchor(nid)}"></a>', "", f"## {label(a.get('name'))}", "", f"- Id: {code(nid)}",
                      "- Valori: " + self.config_text(values, f),
                      "- Letta da: " + (", ".join(f"{self.link(u, f)} ({self.src(cg.attrs(u), f, d.get('line'))})" for u, d in users) or "-")]
            lines += [f"- Nota: {'; '.join(notes)}"] if notes else []
            lines.append("")
        self.report(f, "Configurazione",
                    "Chiavi appSettings di web.config, app.config e delle trasformazioni (Web.Release.config, ...), con i metodi "
                    "che le leggono. I valori di produzione spesso non sono nel repository (trasformazioni al deploy, variabili "
                    "della pipeline, file esterni): quando mancano vanno chiesti; se non sono disponibili, una regola che ne "
                    "dipende dichiara il valore ipotizzato.", lines)

    def report_state(self) -> None:
        f = "reports/implicit-state.md"
        lines = table("Nodo", "Uso", "Chiavi")
        for nid, a in self.nodes("Method", "Page"):
            keys = list(a.get("state_keys") or []) + [f"ViewBag.{k}" for k in a.get("viewbag_keys") or []]
            if a.get("state_usage") or keys:
                usage = ", ".join(a.get("state_usage") or [])
                lines.append(f"| {self.ref(nid, f)} | {cell(usage) or '-'} | {cell(', '.join(keys)) or '-'} |")
        self.report(f, "Stato implicito", "Session, TempData, ViewBag, ViewData, ViewState, Application, Cache e HttpContext.Current: "
                                          "stato che non passa dai parametri e che va ripensato nella migrazione.", lines)

    def report_integrations(self) -> None:
        f = "reports/integrations.md"
        by_cat: dict[str, list[str]] = defaultdict(list)
        for nid, a in self.cg.g.nodes(data=True):
            for c in a.get("integrations") or []:
                by_cat[c].append(nid)
        lines = []
        for cat in sorted(by_cat):
            lines += section(f"{cat} ({len(by_cat[cat])})", bullets(sorted(by_cat[cat]), lambda n: self.ref(n, f), 100))
        self.report(f, "Integrazioni",
                    "Metodi che usano librerie di integrazione (email, http, wcf, code, file system, sql, ...).", lines)

    def report_hotspots(self) -> None:
        f = "reports/hotspots.md"
        methods = [(n, a) for n, a in self.cg.g.nodes(data=True)
                   if a.get("kind") == "Method" and a.get("hasBody") and not a.get("generated")]
        lines = []
        for title, key in (("Complessita' ciclomatica", "complexity"), ("Lunghezza", "loc")):
            top = sorted(methods, key=lambda x: -int(x[1].get(key) or 0))[:50]
            lines += section(title, table("Metodo", "Complessita'", "Righe", "Sorgente") + [
                f"| {self.ref(n, f)} | {a.get('complexity', '-')} | {a.get('loc', '-')} | {self.src(a, f)} |" for n, a in top])
        views = [(n, a) for n, a in self.cg.g.nodes(data=True)
                 if a.get("kind") in ("View", "Page") and (a.get("conditionals") or a.get("inline_code_blocks") or a.get("scripts"))]
        views.sort(key=lambda x: -(int(x[1].get("conditionals") or 0) + int(x[1].get("inline_code_blocks") or 0)))
        rows = [f"| {self.link(n, f, a.get('file') or n)} | {a.get('conditionals', 0)} | {a.get('inline_code_blocks', 0)} "
                f"| {a.get('scripts', 0)} |" for n, a in views[:50]]
        header = table("File", "Condizioni", "Blocchi di codice", "Script")
        lines += section("Logica nelle viste e nelle pagine", header + rows if rows else [])
        self.report(f, "Punti caldi", "Metodi piu' complessi e piu' lunghi, viste e pagine con piu' logica nel markup.", lines)

    def report_gaps(self) -> None:
        cg, f, meta = self.cg, "reports/gaps.md", self.cg.meta
        failures = meta.get("workspaceFailures") or []
        lines = [f"## Avvisi del caricamento dei progetti ({len(failures)})", ""] + [f"- {cell(x)}" for x in failures[:50]] + [""]
        schema = meta.get("sqlSchema")
        if schema is None:
            lines += ["## Schema SQL non fornito", "", "Il grafo e' stato generato con --no-sql: vincoli, trigger e logica delle stored "
                      "procedure non ci sono. Rigeneralo con --sql.", ""]
        else:
            errors = schema.get("parseErrors") or []
            lines += section(f"Blocchi SQL non interpretati ({len(errors)})", [f"- {cell(x)}" for x in errors])
            missing = [n for n, a in self.nodes("Table", "StoredProcedure") if a.get("source") and not a.get("inSchema")]
            lines += section(f"Usati dal codice ma assenti dallo schema ({len(missing)})", [
                "Altro database, oggetti creati a runtime, nomi costruiti male dall'euristica SQL o schema incompleto.", ""]
                + [f"- {self.ref(n, f)}" for n in missing] if missing else [])
        errors = [f"- {self.link(n, f)}: {a['declarationErrors']} errori" for n, a in cg.g.nodes(data=True)
                  if a.get("kind") == "Project" and a.get("declarationErrors")]
        intro = "Di solito riferimenti NuGet non ripristinati: le chiamate verso quei tipi non diventano archi."
        lines += section("Progetti con errori di dichiarazione", [intro, ""] + errors if errors else [])
        if meta.get("prunedDanglingEdges"):
            lines += ["Archi scartati perche' puntavano a nodi assenti (accessor automatici, codice escluso): "
                      f"{meta['prunedDanglingEdges']}", ""]

        def gap(row: tuple[str, Any]) -> str:
            values = row[1] if isinstance(row[1], list) else [row[1]]
            return f"{self.ref(row[0], f)}: " + ", ".join(code(x) for x in values[:10]) + (" ..." if len(values) > 10 else "")

        for key, title in UNRESOLVED:
            rows = sorted(((n, a[key]) for n, a in cg.g.nodes(data=True) if a.get(key)), key=lambda x: x[0])
            lines += section(f"{title} ({len(rows)})", bullets(rows, gap, 200))
        missing = [n for n, a in self.nodes("View") if a.get("unresolved")]
        if missing:
            lines += [f"## Viste non trovate ({len(missing)})", ""] + [f"- {self.ref(n, f)}" for n in missing]
        self.report(f, "Lacune dell'estrazione",
                    "Cio' che l'analisi statica non ha risolto: va verificato a mano o chiesto agli esperti.", lines)

    # ------------------------------------------------------------------ indice generale

    def write_index(self) -> None:
        cg, f, meta = self.cg, "INDEX.md", self.cg.meta
        counts: dict[str, int] = defaultdict(int)
        for _, a in cg.g.nodes(data=True):
            counts[a.get("kind") or "?"] += 1
        lines = [f"# Grafo del codice: {label(meta.get('input') or os.path.basename(cg.graph_path))}", "",
                 f"Generato da `tools/graph_docs.py` dal grafo {code(os.path.basename(cg.graph_path))} "
                 f"({meta.get('tool') or '?'}, {meta.get('generatedAt') or 'data non indicata'}). "
                 f"Riassunti LLM disponibili: {len(cg.summaries)}.", "",
                 "## Come usare questa documentazione", "",
                 "1. Parti dagli [entry point](routes/INDEX.md): ogni action MVC/Web API, pagina Web Forms, servizio asmx o WCF, "
                 "batch, job, handler di messaggi, modulo HTTP e Global.asax ha un dossier con metodi raggiunti, logica nel "
                 "database, dati e vincoli, viste, stato implicito, configurazione, validazioni ed eccezioni.",
                 "2. I link portano alla scheda di ogni metodo, tipo, tabella, procedura, trigger e vista. Ogni nodo ha un id per "
                 "esteso (`M:`, `T:`, `R:`, `DB:`, `SP:`, `TR:`, `CFG:`, `V:`, `P:`): cercalo in questa cartella per trovare tutti "
                 "i punti in cui compare.",
                 "3. Il codice non e' copiato qui: ogni scheda indica file e riga. Leggi il sorgente solo dove serve.",
                 "4. Gli archi con via `sql` o `ef-tracking` sono dedotti da euristiche: verificali sul sorgente.",
                 "5. Riassunti e casi d'uso marcati \"da validare\" vengono da un LLM: sono indizi, non prove.", ""]
        lines += section("Numeri", table("Nodi", "Quanti") + [f"| {k} | {v} |" for k, v in sorted(counts.items())])
        lines += section("Progetti", table("Progetto", "Errori di dichiarazione")
                         + [f"| {self.link(n, f)} | {a.get('declarationErrors', 0)} |" for n, a in self.nodes("Project")]
                         + ["", "Tutti i tipi: [types/INDEX.md](types/INDEX.md)"])

        groups = sorted(self.groups, key=lambda g: (-len(self.groups[g]), g))
        rows = [f"| [{label(g)}]({self.group_file[g]}) | {len(self.groups[g])} |" for g in groups[:40]]
        rows += [f"| {more(groups, 40)}: vedi [routes/INDEX.md](routes/INDEX.md) | |"] if len(groups) > 40 else []
        total = sum(len(v) for v in self.groups.values())
        lines += section(f"Entry point ({total} rotte in {len(groups)} gruppi)", table("Gruppo", "Rotte") + rows)

        tables = [n for n, a in cg.g.nodes(data=True) if a.get("kind") == "Table"]
        written = sorted(tables, key=lambda t: -len(cg.inc(t, ["WRITES"])))[:15]
        lines += section(f"Dati ({len(tables)} tabelle e viste, {counts.get('StoredProcedure', 0)} stored procedure e funzioni, "
                         f"{counts.get('Trigger', 0)} trigger)", [
            "Tabelle con piu' scrittori: " + (", ".join(self.link(t, f) for t in written) or "-") + ".", "",
            "Tutte le tabelle: [tables/INDEX.md](tables/INDEX.md) · viste e pagine: [views/INDEX.md](views/INDEX.md)"])
        lines += section("Report", [
            "- [Autorizzazioni](reports/authorization.md): regole per rotta e rotte senza autorizzazione dichiarata",
            "- [Proprieta' dei dati](reports/data-ownership.md): chi scrive ogni tabella, per gruppo di entry point",
            "- [Domini candidati](reports/domains.md): gruppi di entry point raggruppati per dati usati, base per la capability map",
            "- [Database](reports/database.md): contenuto dello schema, logica nel database, SQL dinamico, oggetti non raggiunti",
            "- [Configurazione](reports/configuration.md): valori delle chiavi per file e ambiente, chiavi senza valore",
            "- [Stato implicito](reports/implicit-state.md): Session, ViewState, TempData, ViewBag, Cache",
            "- [Integrazioni](reports/integrations.md): email, http, wcf, code, file system, sql",
            "- [Punti caldi](reports/hotspots.md): complessita', lunghezza, logica nel markup",
            "- [Lacune](reports/gaps.md): chiamate, viste, link e gestori non risolti, avvisi di caricamento"])
        schema = meta.get("sqlSchema")
        lines += section("Qualita' dell'estrazione", [
            "- Schema SQL: " + ("non fornito (--no-sql)" if schema is None
                                else f"{schema.get('fileCount', 0)} file, {len(schema.get('parseErrors') or [])} blocchi non interpretati"),
            f"- Avvisi del caricamento dei progetti: {len(meta.get('workspaceFailures') or [])}",
            f"- Metodi con chiamate non risolte: {sum(1 for _, a in cg.g.nodes(data=True) if a.get('unresolved_calls'))}",
            f"- Viste non trovate: {sum(1 for _, a in self.nodes('View') if a.get('unresolved'))}",
            "", "Dettagli in [reports/gaps.md](reports/gaps.md)."])
        self.emit(f, lines)


# ---------------------------------------------------------------------------- main

def prepare_out(out: str, force: bool) -> None:
    if os.path.isdir(out) and os.listdir(out) and not os.path.exists(os.path.join(out, MARKER)) and not force:
        sys.exit(f"{out} non e' vuota e non e' stata generata da graph_docs.py: scegli un'altra cartella o usa --force")
    for section_dir in SECTIONS:
        shutil.rmtree(os.path.join(out, section_dir), ignore_errors=True)
    if os.path.exists(os.path.join(out, "INDEX.md")):
        os.remove(os.path.join(out, "INDEX.md"))
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, MARKER), "w", encoding="utf-8") as f:
        f.write("Cartella generata da tools/graph_docs.py: viene riscritta a ogni esecuzione.\n")


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Documentazione Markdown navigabile generata dal grafo Roslyn")
    ap.add_argument("--graph", required=True, help="graph.json prodotto da roslyn-graph")
    ap.add_argument("--root", help="cartella della solution (default: meta.root del grafo); serve per i link ai sorgenti")
    ap.add_argument("--summaries", help="summaries.jsonl di enrich.py: aggiunge riassunti dei metodi e casi d'uso delle rotte")
    ap.add_argument("--out", default="docs/graph", help="cartella di uscita (default: docs/graph)")
    ap.add_argument("--force", action="store_true",
                    help="scrivi anche in una cartella non vuota mai generata da questo script "
                         "(cancella comunque solo INDEX.md e le sottocartelle routes, types, tables, views, reports)")
    args = ap.parse_args(argv)

    cg = CodeGraph(args.graph, root=args.root, summaries_path=args.summaries)
    prepare_out(args.out, args.force)
    print(f"{Docs(cg, args.out).generate()} file Markdown in {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
