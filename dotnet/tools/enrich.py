"""
Arricchimento bottom-up del grafo con un LLM.

1. Metodi in ordine topologico inverso (prima le foglie): ogni metodo viene riassunto vedendo il proprio
   sorgente + i fatti statici + i riassunti GIA' prodotti dei metodi che chiama. Cosi' il contesto resta
   piccolo e l'informazione risale il call graph.
2. Rotte: per ogni rotta si costruisce il dossier (trace) e si chiede una bozza di specifica as-is del caso d'uso.
   Le bozze vengono salvate anche come Markdown in --specs-dir.

L'esecuzione e' incrementale: un nodo viene rielaborato solo se cambia il suo input (sorgente o riassunti dei chiamati).

Esempi:
    python enrich.py --graph graph.json --root C:\\src\\MiaApp --summaries summaries.jsonl --provider anthropic --scope Orders --routes
    python enrich.py --graph graph.json --summaries summaries.jsonl --provider fake --max-methods 50   # prova a secco
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import hashlib
import json
import os
import re
import sys
import threading
import time
from collections import Counter
from typing import Callable, Iterable

import networkx as nx

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from codegraph import CALL_EDGES, DATA_EDGES, FACT_KEYS, CodeGraph  # noqa: E402

PROMPT_VERSION = "v1"
ROUTE_DEPTH, ROUTE_NODES = 12, 400

METHOD_SYSTEM = """Sei un analista di reverse engineering su applicazioni legacy .NET (ASP.NET MVC, .NET Framework).
Ricevi UN metodo: il suo sorgente con i numeri di riga, i fatti estratti staticamente dal grafo del codice (Roslyn)
e i riassunti gia' prodotti dei metodi che chiama.

Rispondi SOLO con un oggetto JSON valido, senza testo prima o dopo, con questa forma:
{
  "purpose": "una frase: cosa fa il metodo in termini di dominio, non di codice",
  "business_rules": [
    {"rule": "regola in linguaggio di dominio", "kind": "validation|calculation|eligibility|workflow|derivation|authorization",
     "lines": "inizio-fine", "confidence": "CONFIRMED|INFERRED"}
  ],
  "side_effects": ["scritture su database, email, file, chiamate esterne, stato di sessione"],
  "inputs": ["dati in ingresso rilevanti"],
  "outputs": ["risultato o stato prodotto"],
  "domain_terms": ["termini di dominio incontrati"],
  "open_questions": ["cio' che non si puo' stabilire dal codice"],
  "migration_notes": ["dipendenze System.Web, stato implicito, accoppiamenti problematici per la migrazione"]
}

Regole:
- Non inventare. Se non c'e' nulla, lista vuota.
- CONFIRMED solo se la regola e' esplicita nel codice mostrato; se dipende da un metodo di cui vedi solo il riassunto, INFERRED.
- "lines" si riferisce ai numeri di riga mostrati.
- Infrastruttura pura (logging, mapping banale, getter) -> purpose breve e business_rules vuota.
- Lingua delle descrizioni: {lang}."""

ROUTE_SYSTEM = """Sei un analista funzionale che ricostruisce la specifica AS-IS di un caso d'uso di un'applicazione legacy.
Ricevi il dossier di una rotta web, estratto dal grafo del codice: entry point, autorizzazioni, metodi raggiunti
(con i loro riassunti e regole), tabelle lette e scritte, viste e campi, redirect, integrazioni, stato di sessione,
validazioni ed eccezioni.

Rispondi SOLO con un oggetto JSON valido, senza testo prima o dopo, con questa forma:
{
  "use_case": "nome breve del caso d'uso",
  "actor": "chi lo esegue (ruoli dalle autorizzazioni)",
  "goal": "obiettivo di business in una frase",
  "preconditions": ["..."],
  "main_flow": ["passo 1", "passo 2"],
  "alternative_flows": ["condizione -> comportamento"],
  "business_rules": [{"rule": "...", "evidence": ["id dei nodi"], "confidence": "CONFIRMED|INFERRED|GAP"}],
  "data": {"reads": ["tabella: cosa"], "writes": ["tabella: cosa"]},
  "ui": {"views": ["..."], "fields": ["..."]},
  "integrations": ["..."],
  "implicit_state": ["uso di Session/TempData/ViewBag e perche' conta per la migrazione"],
  "nfr_hints": ["transazioni, prestazioni, sicurezza"],
  "open_questions": ["domande per gli esperti di dominio"]
}

Regole:
- Ogni regola cita gli id dei nodi del dossier da cui deriva. Non inventare comportamenti assenti dal dossier.
- Le informazioni mancanti diventano open_questions o regole con confidence GAP.
- Lingua delle descrizioni: {lang}."""


# ---------------------------------------------------------------------------- client LLM

class NonRetryableError(RuntimeError):
    """Errore che un nuovo tentativo non risolve: credenziali, richiesta non valida, risposta troncata da max_tokens."""


class AnthropicLLM:
    """Usa ANTHROPIC_API_KEY. Per Bedrock o Vertex sostituisci il client con anthropic.AnthropicBedrock / AnthropicVertex."""

    def __init__(self, model: str, max_tokens: int):
        import anthropic  # pip install anthropic
        self.client, self.name, self.max_tokens = anthropic.Anthropic(), model, max_tokens
        self.fatal = (anthropic.AuthenticationError, anthropic.PermissionDeniedError,
                      anthropic.BadRequestError, anthropic.NotFoundError)

    def complete(self, system: str, user: str) -> str:
        try:
            resp = self.client.messages.create(model=self.name, max_tokens=self.max_tokens, system=system,
                                               messages=[{"role": "user", "content": user}])
        except self.fatal as ex:
            raise NonRetryableError(str(ex)) from ex
        if resp.stop_reason == "max_tokens":
            raise NonRetryableError(f"risposta troncata a {self.max_tokens} token: aumenta --max-tokens")
        return "".join(b.text for b in resp.content if getattr(b, "type", "") == "text")


class OpenAICompatibleLLM:
    """Qualsiasi endpoint compatibile OpenAI (Azure OpenAI, vLLM on-premise, ecc.). Usa OPENAI_API_KEY e --base-url."""

    def __init__(self, model: str, max_tokens: int, base_url: str | None):
        import openai  # pip install openai
        self.client, self.name, self.max_tokens = openai.OpenAI(base_url=base_url), model, max_tokens
        self.fatal = (openai.AuthenticationError, openai.PermissionDeniedError,
                      openai.BadRequestError, openai.NotFoundError)

    def complete(self, system: str, user: str) -> str:
        try:
            resp = self.client.chat.completions.create(model=self.name, max_tokens=self.max_tokens, messages=[
                {"role": "system", "content": system}, {"role": "user", "content": user}])
        except self.fatal as ex:
            raise NonRetryableError(str(ex)) from ex
        if resp.choices[0].finish_reason == "length":
            raise NonRetryableError(f"risposta troncata a {self.max_tokens} token: aumenta --max-tokens")
        return resp.choices[0].message.content or ""


class FakeLLM:
    """Nessuna chiamata di rete: produce JSON plausibili dai fatti nel prompt. Serve per provare la pipeline e i costi."""
    name = "fake"

    def complete(self, system: str, user: str) -> str:
        if "use_case" in system:
            rid = re.search(r'"id": "([^"]+)"', user)
            return json.dumps({"use_case": rid.group(1) if rid else "?", "actor": "?", "goal": "(fake)", "main_flow": [],
                               "business_rules": [], "open_questions": ["(fake) eseguire con un LLM reale"]})
        node = re.search(r"## Nodo (\S+)", user)
        validations = re.search(r'"validations": (\[.*?\n \])', user, re.S)
        rules = [{"rule": f"(fake) {v.get('message')}", "kind": "validation", "lines": "?", "confidence": "INFERRED"}
                 for v in json.loads(validations.group(1) if validations else "[]")]
        return json.dumps({"purpose": f"(fake) riassunto di {node.group(1).split('.')[-1].split('(')[0] if node else '?'}",
                           "business_rules": rules, "side_effects": [], "inputs": [], "outputs": [], "domain_terms": [],
                           "open_questions": [], "migration_notes": []})


def make_llm(args):
    if args.provider == "anthropic":
        return AnthropicLLM(args.model, args.max_tokens)
    if args.provider == "openai":
        return OpenAICompatibleLLM(args.model, args.max_tokens, args.base_url)
    return FakeLLM()


def parse_json(text: str) -> dict:
    text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text.strip())
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        start, end = text.find("{"), text.rfind("}")
        if 0 <= start < end:
            return json.loads(text[start:end + 1])
        raise


def call_with_retry(llm, system: str, user: str, attempts: int = 4) -> dict:
    for i in range(attempts):
        try:
            return parse_json(llm.complete(system, user))
        except NonRetryableError:
            raise
        except Exception as ex:  # rete, rate limit, JSON non valido
            if i == attempts - 1:
                raise RuntimeError(f"LLM fallito dopo {attempts} tentativi: {ex}") from ex
            time.sleep(2.0 * 2 ** i)


def run_parallel(items: Iterable, work: Callable[[str], str], concurrency: int) -> Counter:
    """Esegue work(id) in parallelo e conta gli esiti; un elemento fallito non ferma gli altri."""
    counts: Counter = Counter()
    with cf.ThreadPoolExecutor(max_workers=concurrency) as pool:
        futures = {pool.submit(work, nid): nid for nid in items}
        for fut in cf.as_completed(futures):
            try:
                counts[fut.result()] += 1
            except Exception as ex:
                counts["errori"] += 1
                print(f"[errore] {futures[fut]}: {ex}", file=sys.stderr)
    return counts


def is_cached(rec: dict | None, h: str, llm) -> bool:
    """Un riassunto prodotto dal provider fake non vale come cache per un provider reale."""
    return bool(rec) and rec.get("hash") == h and (rec.get("model") != "fake" or llm.name == "fake")


# ---------------------------------------------------------------------------- metodi

def body_methods(cg: CodeGraph) -> set[str]:
    return {n for n, a in cg.g.nodes(data=True) if a.get("kind") == "Method" and a.get("hasBody") and not a.get("generated")}


def effective_callees(cg: CodeGraph, nid: str, with_body: set[str]) -> list[str]:
    """Chiamati reali: una chiamata a un metodo astratto o di interfaccia vale per le sue implementazioni."""
    out = []
    for v, _ in cg.out(nid, CALL_EDGES):
        out += [v] if v in with_body else [w for w, _ in cg.out(v, ["DISPATCHES_TO"]) if w in with_body]
    return list(dict.fromkeys(x for x in out if x != nid))


def method_generations(cg: CodeGraph, scope: set[str]) -> tuple[list[list[str]], dict[str, int]]:
    """Generazioni di metodi: ognuna dipende solo dalle precedenti. I cicli di chiamate finiscono nella stessa generazione."""
    h = nx.DiGraph()
    h.add_nodes_from(scope)
    h.add_edges_from((m, c) for m in scope for c in effective_callees(cg, m, scope))
    cond = nx.condensation(h)
    generations = nx.topological_generations(cond.reverse(copy=False))
    gens = [sorted(m for scc in gen for m in cond.nodes[scc]["members"]) for gen in generations]
    return gens, cond.graph["mapping"]


def select_routes(cg: CodeGraph, pattern: str | None) -> list[str]:
    """Rotte il cui id o template contiene pattern, o il cui controller e' pattern; tutte se pattern e' None."""
    p = (pattern or "").lower()
    return sorted((nid for nid, a in cg.g.nodes(data=True) if a.get("kind") == "Route"
                   and (p in f"{nid} {a.get('template')}".lower() or p == str(a.get("controller", "")).lower())),
                  key=lambda r: (str(cg.attrs(r).get("controller")), str(cg.attrs(r).get("action")), r))


def method_scope(cg: CodeGraph, pattern: str, with_body: set[str]) -> set[str]:
    """Metodi raggiungibili dalle rotte selezionate, piu' tutto cio' che chiamano (servono i riassunti delle dipendenze)."""
    scope = {m["id"] for r in select_routes(cg, pattern) for m in cg.trace(r, ROUTE_DEPTH, 5000)["methods"]} & with_body
    frontier = list(scope)
    while frontier:
        for c in effective_callees(cg, frontier.pop(), with_body):
            if c not in scope:
                scope.add(c)
                frontier.append(c)
    return scope


def is_trivial(cg: CodeGraph, nid: str, callees: list[str]) -> str | None:
    """Riassunto senza LLM per accessor, costanti e deleghe pure."""
    a = cg.attrs(nid)
    if int(a.get("loc", 99)) > 3 or cg.out(nid, DATA_EDGES) or any(a.get(k) for k in FACT_KEYS if k != "external_calls"):
        return None
    if not callees:
        return "metodo banale (accessor o costante)"
    if len(callees) == 1:
        s = cg.summary_of(callees[0])
        return f"delega a {callees[0]}" + (f": {s.get('purpose')}" if isinstance(s, dict) and s.get("purpose") else "")
    return None


def input_hash(cg: CodeGraph, nid: str, deps: list[str], peers: set[str] = frozenset()) -> str:
    """peers: chiamati nello stesso ciclo di chiamate. Per loro conta il sorgente, non il riassunto: con la ricorsione
    mutua ogni riassunto cambierebbe l'hash dell'altro e la cache non sarebbe mai valida."""
    h = hashlib.sha256(PROMPT_VERSION.encode())
    h.update(cg.source_hash(nid).encode())
    for d in sorted(deps):
        h.update((("src:" + cg.source_hash(d)) if d in peers else (cg.summaries.get(d, {}).get("hash") or "-")).encode())
    return h.hexdigest()[:16]


def enrich_methods(cg: CodeGraph, llm, args) -> None:
    with_body = body_methods(cg)
    scope = method_scope(cg, args.scope, with_body) if args.scope else with_body
    gens, scc_of = method_generations(cg, scope)
    total = sum(len(g) for g in gens)
    print(f"metodi da considerare: {total} in {len(gens)} generazioni", file=sys.stderr)
    budget = total if args.max_methods is None else args.max_methods
    system = METHOD_SYSTEM.replace("{lang}", args.lang)
    lock = threading.Lock()

    def work(nid: str) -> str:
        callees = effective_callees(cg, nid, with_body)
        h = input_hash(cg, nid, callees, {c for c in callees if scc_of.get(c) == scc_of.get(nid)})
        if is_cached(cg.summaries.get(nid), h, llm):
            return "in cache"
        trivial = is_trivial(cg, nid, callees)
        if trivial:
            summary, model = {"purpose": trivial, "business_rules": [], "trivial": True}, "rule"
        else:
            prompt = cg.context_pack(nid, budget_chars=args.budget_chars)
            if args.dump_prompts:
                with open(os.path.join(args.dump_prompts, re.sub(r"[^\w.-]+", "_", nid)[:150] + ".md"), "w", encoding="utf-8") as f:
                    f.write(prompt)
            summary, model = call_with_retry(llm, system, prompt), llm.name
        with lock:
            cg.save_summary(nid, summary, model, h)
        return "elaborati"

    counts: Counter = Counter()
    for gi, gen in enumerate(gens, 1):
        todo = gen[:max(0, budget - counts["elaborati"])]
        if not todo:
            break
        counts += run_parallel(todo, work, args.concurrency)
        print(f"  generazione {gi}/{len(gens)}: {dict(counts)}", file=sys.stderr)


# ---------------------------------------------------------------------------- rotte

def route_dossier(cg: CodeGraph, trace: dict, budget_chars: int) -> str:
    """Dossier della rotta con i riassunti dei metodi raggiunti al posto delle loro schede."""
    methods = []
    for m in trace["methods"]:
        s = cg.summary_of(m["id"]) or {}
        item = {"id": m["id"], "depth": m["depth"], "purpose": s.get("purpose")}
        item.update((k, v) for k, v in (("rules", s.get("business_rules")), ("side_effects", s.get("side_effects")),
                                        ("flags", m.get("flags"))) if v)
        methods.append(item)
    dossier = {k: v for k, v in trace.items() if k not in ("methods", "note")} | {"methods": methods}
    text = "## Dossier rotta\n" + json.dumps(dossier, ensure_ascii=False, indent=1)
    return text if len(text) <= budget_chars else text[:budget_chars] + "\n[...troncato...]"


def enrich_routes(cg: CodeGraph, llm, args) -> None:
    routes = select_routes(cg, args.scope)
    system = ROUTE_SYSTEM.replace("{lang}", args.lang)
    lock = threading.Lock()
    os.makedirs(args.specs_dir, exist_ok=True)
    print(f"rotte da specificare: {len(routes)}", file=sys.stderr)

    def work(rid: str) -> str:
        trace = cg.trace(rid, ROUTE_DEPTH, ROUTE_NODES)
        prompt = route_dossier(cg, trace, args.budget_chars * 2)
        # l'hash e' sull'input reale: cambia se cambiano autorizzazioni, tabelle, viste o riassunti dei metodi
        h = hashlib.sha256((PROMPT_VERSION + system + prompt).encode()).hexdigest()[:16]
        rec = cg.summaries.get(rid)
        if is_cached(rec, h, llm):
            if os.path.exists(os.path.join(args.specs_dir, spec_filename(rid))):
                return "in cache"
            write_spec_markdown(args.specs_dir, cg, rid, rec["summary"], trace)
            return "riscritte"
        spec = call_with_retry(llm, system, prompt)
        if not isinstance(spec, dict):
            raise ValueError(f"la risposta non e' un oggetto JSON: {type(spec).__name__}")
        write_spec_markdown(args.specs_dir, cg, rid, spec, trace)  # prima il file: se fallisce, la rotta non va in cache
        with lock:
            cg.save_summary(rid, spec, llm.name, h)
        return "elaborate"

    print(f"  rotte: {dict(run_parallel(routes, work, args.concurrency))}", file=sys.stderr)


def spec_filename(rid: str) -> str:
    return re.sub(r"[^\w.-]+", "_", rid.replace("R:", ""))[:120].strip("_") + ".md"


# risposte LLM fuori schema: oggetti al posto di stringhe, stringhe al posto di liste
def _text(x) -> str:
    return x if isinstance(x, str) else json.dumps(x, ensure_ascii=False)


def _items(x) -> list:
    return [] if x is None else list(x) if isinstance(x, (list, tuple)) else [x]


def _cell(x) -> str:
    return _text(x).replace("|", "\\|").replace("\n", " ")


def write_spec_markdown(folder: str, cg: CodeGraph, rid: str, spec: dict, trace: dict) -> None:
    def bullets(key):
        return "\n".join(f"- {_text(i)}" for i in _items(spec.get(key))) or "- (nessuno)"

    def joined(section, key):
        d = spec.get(section) if isinstance(spec.get(section), dict) else {}
        return ", ".join(_text(i) for i in _items(d.get(key))) or "-"

    rules = [r if isinstance(r, dict) else {"rule": r} for r in _items(spec.get("business_rules"))]
    handler = next((v for v, _ in cg.out(rid, ["HANDLED_BY"])), None)
    flow = "\n".join(f"{i}. {_text(s)}" for i, s in enumerate(_items(spec.get("main_flow")), 1)) or "- (nessuno)"
    rule_rows = [f"| {_cell(r.get('rule', ''))} | {', '.join(f'`{_cell(e)}`' for e in _items(r.get('evidence')))} "
                 f"| {_cell(r.get('confidence', ''))} |" for r in rules]
    lines = [
        f"# {spec.get('use_case') or rid}",
        "",
        "Stato: BOZZA AS-IS generata da LLM, da validare con gli esperti di dominio.  ",
        f"Rotta: `{rid}`  ",
        f"Handler: `{handler}`  ",
        f"Autorizzazione: {cg.attrs(rid).get('authorization') or '-'}",
        "",
        f"**Attore:** {spec.get('actor', '-')}  ",
        f"**Obiettivo:** {spec.get('goal', '-')}",
        "", "## Precondizioni", bullets("preconditions"),
        "", "## Flusso principale", flow,
        "", "## Flussi alternativi", bullets("alternative_flows"),
        "", "## Regole di business", "",
        "| Regola | Evidenze | Stato |", "| --- | --- | --- |",
        *rule_rows,
        "", "## Dati",
        f"Letture: {joined('data', 'reads')}  ",
        f"Scritture: {joined('data', 'writes')}",
        "", "## Interfaccia utente",
        f"Viste: {joined('ui', 'views')}  ",
        f"Campi: {joined('ui', 'fields')}",
        "", "## Integrazioni", bullets("integrations"),
        "", "## Stato implicito", bullets("implicit_state"),
        "", "## Indicazioni non funzionali", bullets("nfr_hints"),
        "", "## Domande aperte", bullets("open_questions"),
        "", "## Evidenze dal grafo",
        f"Metodi raggiunti: {len(trace['methods'])}  ",
        f"Tabelle: {', '.join(sorted(trace['tables'])) or '-'}  ",
        f"Stored procedure: {', '.join(sorted(trace['stored_procedures'])) or '-'}",
        "",
    ]
    with open(os.path.join(folder, spec_filename(rid)), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Arricchimento bottom-up del grafo con un LLM")
    ap.add_argument("--graph", required=True)
    ap.add_argument("--root", help="cartella della solution (default: meta.root del grafo)")
    ap.add_argument("--summaries", default="summaries.jsonl", help="file JSONL di uscita (append, incrementale)")
    ap.add_argument("--provider", required=True, choices=["anthropic", "openai", "fake"],
                    help="anthropic e openai inviano il sorgente al modello; fake non fa chiamate di rete")
    ap.add_argument("--model", default=os.environ.get("ENRICH_MODEL", "claude-sonnet-5-5"))
    ap.add_argument("--base-url", help="endpoint per --provider openai (es. vLLM on-premise)")
    ap.add_argument("--max-tokens", type=int, default=2000)
    ap.add_argument("--concurrency", type=int, default=4)
    ap.add_argument("--budget-chars", type=int, default=24000, help="dimensione massima del contesto per metodo")
    ap.add_argument("--scope", help="solo metodi raggiungibili da rotte/controller che contengono questo testo")
    ap.add_argument("--max-methods", type=int, help="limite di metodi da elaborare (prove e stime di costo)")
    ap.add_argument("--routes", action="store_true", help="dopo i metodi, genera le bozze di specifica per le rotte")
    ap.add_argument("--routes-only", action="store_true", help="salta i metodi (usa i riassunti gia' presenti)")
    ap.add_argument("--specs-dir", default="specs-as-is")
    ap.add_argument("--lang", default="italiano")
    ap.add_argument("--dump-prompts", help="salva i prompt dei metodi in questa cartella (per revisione)")
    ap.add_argument("--no-redact", action="store_true", help="non mascherare password e chiavi nel sorgente inviato")
    args = ap.parse_args(argv)

    if args.dump_prompts:
        os.makedirs(args.dump_prompts, exist_ok=True)
    cg = CodeGraph(args.graph, root=args.root, summaries_path=args.summaries, redact=not args.no_redact)
    llm = make_llm(args)
    if not args.routes_only:
        enrich_methods(cg, llm, args)
    if args.routes or args.routes_only:
        enrich_routes(cg, llm, args)


if __name__ == "__main__":
    main()
