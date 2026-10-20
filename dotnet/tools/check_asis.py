"""
Controlli deterministici sui deliverable as-is (docs/as-is), senza LLM:

1. formato del catalogo delle regole: id unici, stato e tipo ammessi, capability presente nella capability map;
2. evidenze: gli id citati esistono nel grafo, i file:riga esistono e cadono nel metodo citato, una regola CONFIRMED
   cita il sorgente, un GAP ha la sua domanda, un arco euristico (sql, ef-tracking) e' confermato da una riga;
3. valori: numeri, percentuali e testi tra virgolette della regola compaiono nelle righe citate (o nei valori di
   configurazione e nei vincoli degli oggetti citati);
4. copertura: per ogni caso d'uso, i fatti del grafo delle sue rotte che nessuno spiega. Validazioni, eccezioni e
   vincoli CHECK vanno citati da una regola o dichiarati "escluso:"; per scritture, chiamate a procedure e chiavi di
   configurazione basta che il caso d'uso nomini la tabella, la procedura o la chiave;
5. versione del grafo citata dai casi d'uso;
6. tracciabilita' con i test (tag @BR-xxxx negli scenari .feature) ed esiti registrati nella colonna Verifica;
7. revisioni degli esperti (punteggi V/C/L/U da 1 a 4).

    python check_asis.py --graph C:\\analisi\\graph.json --docs C:\\src\\MiaApp\\docs\\as-is --tests C:\\src\\MiaApp\\tests\\as-is

Scrive <docs>/verifica/controlli.md ed esce con 1 se ci sono errori (con --strict anche se ci sono avvisi).
Non modifica nessun deliverable.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asis  # noqa: E402
from codegraph import CodeGraph, _read_lines  # noqa: E402

LEVELS = ("errore", "avviso", "info")
WINDOW = 3            # righe intorno a un file:riga in cui cercare i valori citati e i fatti coperti
MAX_SPAN = 400        # oltre, il corpo di un nodo citato e' troppo grande per valere come evidenza dei valori
HEURISTIC = ("sql", "ef-tracking")


class Checker:
    def __init__(self, cg: CodeGraph, docs: str, tests: str | None):
        self.cg, self.docs, self.tests = cg, docs, tests
        self.findings: list[tuple[str, str, str]] = []       # livello, dove, messaggio
        self.coverage: list[tuple[str, list[str], list[str]]] = []
        self._lines: dict[str, list[str] | None] = {}
        self.rules: list[asis.Rule] = []
        self.stats: Counter = Counter()

    def add(self, level: str, where: str, message: str) -> None:
        self.findings.append((level, where, message))

    # ------------------------------------------------------------------ sorgenti

    def lines_of(self, rel: str) -> list[str] | None:
        if rel not in self._lines:
            path = os.path.join(self.cg.root, rel)
            ok = self.cg._inside_root(path) and os.path.isfile(path)
            self._lines[rel] = _read_lines(path) if ok else None
        return self._lines[rel]

    def window(self, ref: asis.Ref, pad: int = WINDOW) -> str:
        lines = self.lines_of(ref.file) or []
        return "\n".join(lines[max(0, ref.start - 1 - pad):ref.end + pad])

    def node_text(self, nid: str) -> str:
        """Testo in cui una regola puo' trovare i suoi valori: corpo del nodo, vincoli, valori di configurazione."""
        a = self.cg.attrs(nid)
        parts = []
        start, end = int(a.get("startLine") or 0), int(a.get("endLine") or 0)
        if a.get("file") and start and end - start <= MAX_SPAN:
            parts.append(self.window(asis.Ref(a["file"], start, end), 0))
        parts += [str(c.get("expression") or "") for c in a.get("constraints") or []]
        parts += [str(c.get("default") or "") for c in a.get("columns") or []]
        parts += [str(v.get("value") or "") for v in a.get("values") or []]
        parts += [" ".join(p.get("attributes") or []) for p in a.get("properties") or []]
        for cfg, _ in self.cg.out(nid, ["USES_CONFIG"]):
            parts += [str(v.get("value") or "") for v in self.cg.attrs(cfg).get("values") or []]
        return "\n".join(parts)

    # ------------------------------------------------------------------ catalogo

    def check_rules(self, path: str) -> None:
        if not os.path.exists(path):
            self.add("errore", "rules.md", f"catalogo non trovato: {path}")
            return
        rules, _, problems = asis.parse_rules(path)
        for p in problems:
            self.add("errore", "rules.md", p)
        self.rules = rules
        capabilities = asis.capability_names(os.path.join(self.docs, "capabilities.md"))
        if not capabilities:
            self.add("info", "capabilities.md", "capability map assente o senza capability di livello 2 (titoli ###)")
        if not os.path.exists(os.path.join(self.docs, "glossary.md")):
            self.add("info", "glossary.md", "glossario assente")
        seen: dict[str, int] = {}
        for r in rules:
            where = f"{r.id} (rules.md:{r.line})"
            self.stats[r.status or "?"] += 1
            if not re.fullmatch(r"BR-\d{4,}", r.id):
                self.add("errore", where, "id non nel formato BR-0001")
            if r.id in seen:
                self.add("errore", where, f"id duplicato (gia' alla riga {seen[r.id]})")
            seen.setdefault(r.id, r.line)
            if r.status not in asis.STATUSES:
                self.add("errore", where, f"stato {r.get('status')!r} non ammesso: {', '.join(asis.STATUSES)}")
            if r.get("type").lower() not in asis.TYPES:
                self.add("avviso", where, f"tipo {r.get('type')!r} non tra {', '.join(asis.TYPES)}")
            if capabilities and r.get("capability").lower() not in capabilities:
                self.add("avviso", where, f"capability {r.get('capability')!r} non presente in capabilities.md")
            if re.search(r"`(?:M|T|DB|SP|TR|V|P):|\w+\.\w+\(", r.text):
                self.add("avviso", where, "il testo della regola contiene nomi tecnici: spostali nelle evidenze")
            self.check_evidence(r, where)

    def check_evidence(self, r: asis.Rule, where: str) -> None:
        cg = self.cg
        if r.status == "GAP":
            if r.get("questions") in ("", "-"):
                self.add("errore", where, "GAP senza domanda aperta")
        elif not r.node_ids and not r.refs:
            self.add("errore", where, f"{r.status} senza evidenze")
        elif r.status == "CONFIRMED" and not r.refs:
            self.add("errore", where, "CONFIRMED senza file:riga: una regola confermata cita le righe lette")

        spans = []
        for nid in r.node_ids:
            if nid not in cg.g:
                self.add("errore", where, f"id non presente nel grafo: {nid}")
                continue
            a = cg.attrs(nid)
            if a.get("file") and a.get("startLine"):
                spans.append((nid, a["file"], int(a["startLine"]), int(a.get("endLine") or a["startLine"])))
        for ref in r.refs:
            lines = self.lines_of(ref.file)
            if lines is None:
                self.add("errore", where, f"file non trovato nella radice dei sorgenti: {ref.file}")
                continue
            if ref.end > len(lines) or ref.start < 1:
                self.add("errore", where, f"{ref}: oltre la fine del file ({len(lines)} righe)")
                continue
            same_file = [s for s in spans if s[1] == ref.file]
            if same_file and not any(s[2] <= ref.start and ref.end <= s[3] for s in same_file):
                self.add("avviso", where, f"{ref} non cade in nessuno dei nodi citati di quel file")

        # arco euristico usato come evidenza di una regola confermata: serve la riga che lo conferma
        if r.status == "CONFIRMED":
            for nid, file, start, end in spans:
                for t, d in cg.out(nid, ["READS", "WRITES"]):
                    if t in r.node_ids and d.get("via") in HEURISTIC:
                        if not any(ref.file == file and start <= ref.start <= end for ref in r.refs):
                            self.add("avviso", where, f"arco euristico {d['type']} {nid} -> {t} (via {d['via']}): cita la riga che lo conferma")

        # valori citati nel testo della regola
        text = "\n".join([self.window(ref) for ref in r.refs] + [self.node_text(n) for n in r.node_ids if n in cg.g])
        if r.status != "GAP" and text.strip():
            for kind, original, value in asis.literals(r.text):
                if not asis.literal_found(kind, value, text):
                    self.add("avviso", where, f"valore {original!r} non trovato nelle righe e nei nodi citati")

    # ------------------------------------------------------------------ casi d'uso

    def route_facts(self, rid: str) -> list[tuple[str, int, str, str | None]]:
        """
        Fatti del dossier di una rotta con la loro posizione e, per i dati, il nome che basta citare nel caso d'uso.
        Sono cio' che il caso d'uso deve spiegare: con una regola, citandoli, o escludendoli con un motivo.
        """
        cg, t = self.cg, self.cg.trace(rid, 12, 400)
        facts = []

        def pos(node: str, line, file=None) -> tuple[str, int] | None:
            f = file or cg.attrs(node).get("file")
            return (f, int(line)) if f and line else None

        for v in t["validations"]:
            p = pos(v["method"], v.get("line"), v.get("file"))
            facts += [(*p, f"validazione {v.get('field') or ''}: {v.get('message') or v.get('rule') or ''}".strip(), None)] if p else []
        for x in t["throws"]:
            p = pos(x["method"], x.get("line"))
            facts += [(*p, f"eccezione {x.get('type')} «{x.get('message') or ''}»", None)] if p else []
        for c in t["constraints"]:
            if c.get("kind") == "CHECK" and c.get("file") and c.get("line"):
                facts.append((c["file"], int(c["line"]), f"vincolo CHECK su {cg.attrs(c['table']).get('name')}: {c.get('expression')}", None))
        reached = [m["id"] for m in t["methods"]] + [x["id"] for x in t["db_logic"]]
        for m in reached:
            for target, d in cg.out(m, ["WRITES", "CALLS_PROC", "USES_CONFIG"]):
                p = pos(m, d.get("line"))
                what = {"WRITES": "scrittura su", "CALLS_PROC": "chiamata a", "USES_CONFIG": "configurazione"}[d["type"]]
                name = str(cg.attrs(target).get("name") or target)
                facts += [(*p, f"{what} {name}", name)] if p else []
        return sorted(set(facts))

    def check_documents(self) -> None:
        known = {r.id for r in self.rules}
        refs = [ref for r in self.rules for ref in r.refs]
        cited: set[str] = set()
        version = self.cg.meta.get("generatedAt")
        for path in asis.asis_documents(self.docs):
            rel = os.path.relpath(path, self.docs).replace(os.sep, "/")
            lines = asis.read_text(path)
            text = "\n".join(lines)
            ids = set(asis.RULE_ID.findall(text))
            cited |= ids
            for missing in sorted(ids - known):
                self.add("errore", rel, f"cita {missing}, che non e' nel catalogo")
            if "Generato da" not in text and "versione del grafo" not in text.lower():
                self.add("avviso", rel, "manca la versione del grafo nella sezione Copertura (riga \"Generato da\" di docs/graph/INDEX.md)")
            elif version and version not in text:
                self.add("avviso", rel, "scritto su un'altra versione del grafo: ricontrolla le evidenze sul grafo attuale")
            routes = [n for n in asis.evidence_of(text)[0] if n.startswith("R:")]
            if not routes:
                self.add("avviso", rel, "non cita l'id di nessuna rotta: la copertura non si puo' controllare")
                continue
            excluded = asis.exclusions(lines)
            uncovered = []
            for rid in routes:
                if rid not in self.cg.g:
                    self.add("errore", rel, f"rotta non presente nel grafo: {rid}")
                    continue
                for file, line, label, name in self.route_facts(rid):
                    near = lambda x, pad: x.file == file and x.start - pad <= line <= x.end + pad  # noqa: E731
                    named = name is not None and re.search(r"(?<!\w)" + re.escape(name) + r"(?!\w)", text, re.IGNORECASE)
                    if not named and not any(near(x, WINDOW) for x in refs) and not any(near(x, 0) for x in excluded):
                        uncovered.append(f"`{file}:{line}` {label} ({rid[2:]})")
            uncovered = list(dict.fromkeys(uncovered))
            self.coverage.append((rel, routes, uncovered))
            if uncovered:
                self.add("avviso", rel, f"{len(uncovered)} fatti del grafo non coperti da regole ne' esclusi (vedi Copertura)")
        for r in self.rules:
            if r.id not in cited:
                self.add("info", f"{r.id} (rules.md:{r.line})", "non citata da nessun caso d'uso")

    # ------------------------------------------------------------------ test e revisioni

    def check_tests_and_reviews(self) -> None:
        scenarios = asis.parse_features(self.tests) if self.tests and os.path.isdir(self.tests) else []
        by_rule: dict[str, list[asis.Scenario]] = defaultdict(list)
        known = {r.id for r in self.rules}
        for s in scenarios:
            for tag in s.tags:
                if asis.RULE_ID.fullmatch(tag[1:]):
                    by_rule[tag[1:]].append(s)
                    if tag[1:] not in known:
                        rel = os.path.relpath(s.file, self.tests or ".")
                        self.add("errore", f"{rel}:{s.line}", f"lo scenario {s.name!r} cita {tag[1:]}, che non e' nel catalogo")
        self.stats["scenari"] = len(scenarios)
        for r in self.rules:
            where = f"{r.id} (rules.md:{r.line})"
            outcome = r.get("verification").lower()
            if by_rule.get(r.id):
                self.stats["con test"] += 1
            if "fallisce" in outcome:
                self.stats["test che falliscono"] += 1
                self.add("avviso", where, "il test sul legacy fallisce: la regola e' da correggere o il legacy ha un comportamento da chiarire")
            elif "passa" in outcome:
                self.stats["test che passano"] += 1
            if outcome and outcome != "-" and not by_rule.get(r.id) and self.tests:
                self.add("avviso", where, "colonna Verifica compilata ma nessuno scenario ha il tag della regola")
            elif by_rule.get(r.id) and outcome in ("", "-"):
                self.add("info", where, "ha uno scenario ma l'esito sul legacy non e' registrato in Verifica")
            elif r.status == "CONFIRMED" and not by_rule.get(r.id) and self.tests:
                self.add("info", where, "CONFIRMED senza scenario di test")
            review = r.get("review")
            scores = re.findall(r"\b([VCLU])\s*([1-4])\b", review)
            if scores:
                self.stats["revisionate"] += 1
                low = [f"{k}{v}" for k, v in scores if int(v) <= 2]
                if low:
                    self.add("avviso", where, f"revisione con punteggi bassi: {' '.join(low)}")

    # ------------------------------------------------------------------ report

    def report(self) -> list[str]:
        meta = self.cg.meta
        counts = Counter(level for level, _, _ in self.findings)
        lines = ["# Controlli sui deliverable as-is", "",
                 f"Generato da `tools/check_asis.py` il {_dt.date.today().isoformat()} sul grafo "
                 f"({meta.get('tool') or '?'}, {meta.get('generatedAt') or 'data non indicata'}). "
                 "Controlli deterministici: non dicono se una regola e' giusta, dicono se e' verificabile e dove guardare.", "",
                 "## Riepilogo", "", "| Voce | Quanti |", "| --- | --- |",
                 f"| Regole | {len(self.rules)} |"]
        lines += [f"| {k} | {self.stats[k]} |" for k in (*asis.STATUSES, "con test", "test che passano", "test che falliscono",
                                                        "revisionate", "scenari") if self.stats[k]]
        lines += [f"| {level.capitalize()} | {counts[level]} |" for level in LEVELS]
        for level in LEVELS:
            items = [(w, m) for lv, w, m in self.findings if lv == level]
            if items:
                lines += ["", f"## {level.capitalize()} ({len(items)})", ""] + [f"- {w}: {m}" for w, m in items]
        if self.coverage:
            lines += ["", "## Copertura dei casi d'uso", "",
                      "Fatti del grafo delle rotte del caso d'uso che nessuna regola cita (entro "
                      f"{WINDOW} righe), che il caso d'uso non nomina (dati, procedure, configurazione) e che non sono "
                      "dichiarati \"escluso:\". Ognuno e' una regola mancante o un fatto da escludere con il suo motivo.", ""]
            for rel, routes, uncovered in self.coverage:
                lines += [f"### {rel}", "", "Rotte: " + ", ".join(f"`{r}`" for r in routes), ""]
                lines += ([f"- {u}" for u in uncovered] if uncovered else ["Tutti i fatti sono coperti o esclusi."]) + [""]
        return lines


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Controlli deterministici sui deliverable as-is")
    ap.add_argument("--graph", required=True, help="graph.json prodotto da roslyn-graph")
    ap.add_argument("--root", help="cartella della solution (default: meta.root del grafo)")
    ap.add_argument("--docs", default="docs/as-is", help="cartella dei deliverable (default: docs/as-is)")
    ap.add_argument("--tests", help="cartella degli scenari .feature (es. tests/as-is)")
    ap.add_argument("--out", help="report Markdown (default: <docs>/verifica/controlli.md)")
    ap.add_argument("--strict", action="store_true", help="esci con 1 anche se ci sono avvisi")
    args = ap.parse_args(argv)

    cg = CodeGraph(args.graph, root=args.root)
    checker = Checker(cg, args.docs, args.tests)
    checker.check_rules(os.path.join(args.docs, "rules.md"))
    checker.check_documents()
    checker.check_tests_and_reviews()
    out = args.out or os.path.join(args.docs, "verifica", "controlli.md")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(checker.report()) + "\n")
    counts = Counter(level for level, _, _ in checker.findings)
    print(f"{len(checker.rules)} regole: {counts['errore']} errori, {counts['avviso']} avvisi, {counts['info']} info -> {out}",
          file=sys.stderr)
    return 1 if counts["errore"] or (args.strict and counts["avviso"]) else 0


if __name__ == "__main__":
    sys.exit(main())
