"""
Lettura dei deliverable as-is scritti dall'agente: catalogo delle regole (docs/as-is/rules.md), casi d'uso, capability map
e scenari Gherkin dei test. Lo usano check_asis.py (controlli deterministici) e verify_rules.py (verifica con un LLM).

Formato atteso del catalogo (le colonne si riconoscono dal nome, l'ordine non conta):

    | Id | Regola | Tipo | Stato | Capability | Evidenze | Verifica | Revisione | Domande aperte |

Nelle evidenze: id dei nodi tra backtick (`M:...`, `DB:...`, `R:GET|POST /x` con il | scritto \\|) e riferimenti
file:riga o file:inizio-fine (Core/Services/OrderService.cs:27).
"""
from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from typing import Iterable

RULE_ID = re.compile(r"\bBR-\d{4,}\b")
NODE_ID = re.compile(r"`((?:M|T|R|DB|SP|TR|CFG|V|P|PRJ):[^`]+)`")
SOURCE_EXT = r"cs|cshtml|vbhtml|aspx|ascx|master|asmx|ashx|asax|svc|config|sql|js|ts|vb|xml|json"
FILE_REF = re.compile(r"(?<![\w/.-])((?:[\w.-]+/)*[\w.-]+\.(?:" + SOURCE_EXT + r")):(\d+)(?:-(\d+))?", re.IGNORECASE)
STATUSES = ("CONFIRMED", "INFERRED", "GAP")
TYPES = ("validation", "calculation", "eligibility", "workflow", "derivation", "authorization")
COLUMNS = {"id": "id", "regola": "rule", "tipo": "type", "stato": "status", "capability": "capability",
           "evidenze": "evidence", "verifica": "verification", "revisione": "review", "domande aperte": "questions"}
REQUIRED = ("id", "rule", "type", "status", "capability", "evidence", "questions")


@dataclass
class Ref:
    file: str
    start: int
    end: int

    def __str__(self) -> str:
        return f"{self.file}:{self.start}" + (f"-{self.end}" if self.end != self.start else "")


@dataclass
class Rule:
    id: str
    line: int                                  # riga di rules.md
    cells: dict[str, str]
    node_ids: list[str] = field(default_factory=list)
    refs: list[Ref] = field(default_factory=list)

    def get(self, key: str) -> str:
        return self.cells.get(key, "").strip()

    @property
    def text(self) -> str:
        return self.get("rule")

    @property
    def status(self) -> str:
        return self.get("status").upper()


def split_row(line: str) -> list[str]:
    """Celle di una riga di tabella Markdown; il | protetto (\\|) resta nel testo."""
    body = line.strip()
    body = body[1:] if body.startswith("|") else body
    body = body[:-1] if body.endswith("|") and not body.endswith("\\|") else body
    return [c.replace("\\|", "|").strip() for c in re.split(r"(?<!\\)\|", body)]


def evidence_of(text: str) -> tuple[list[str], list[Ref]]:
    ids = list(dict.fromkeys(m.group(1) for m in NODE_ID.finditer(text)))
    refs = [Ref(m.group(1).replace("\\", "/"), int(m.group(2)), int(m.group(3) or m.group(2))) for m in FILE_REF.finditer(text)]
    return ids, refs


def read_text(path: str) -> list[str]:
    with open(path, encoding="utf-8-sig") as f:
        return f.read().splitlines()


def parse_rules(path: str) -> tuple[list[Rule], list[str], list[str]]:
    """Regole del catalogo, colonne trovate e problemi di formato (tabella assente, colonne mancanti)."""
    lines = read_text(path)
    problems: list[str] = []
    for i, line in enumerate(lines):
        if not line.strip().startswith("|"):
            continue
        header = [c.lower() for c in split_row(line)]
        if "id" not in header or "regola" not in header:
            continue
        keys = [COLUMNS.get(h) for h in header]
        found = [k for k in keys if k]
        problems += [f"colonna mancante nel catalogo: {k}" for k in REQUIRED if k not in found]
        rules = []
        for j in range(i + 2, len(lines)):
            if not lines[j].strip().startswith("|"):
                break
            values = split_row(lines[j])
            cells = {k: values[n] if n < len(values) else "" for n, k in enumerate(keys) if k}
            if not cells.get("id"):
                continue
            ids, refs = evidence_of(cells.get("evidence", ""))
            rules.append(Rule(cells["id"].strip(), j + 1, cells, ids, refs))
        return rules, found, problems
    return [], [], ["nessuna tabella con le colonne Id e Regola"]


def asis_documents(folder: str) -> list[str]:
    """Casi d'uso: i Markdown di docs/as-is tranne catalogo, capability map, glossario e report di verifica."""
    skip = {"rules.md", "capabilities.md", "glossary.md"}
    docs = []
    for dirpath, dirnames, filenames in os.walk(folder):
        dirnames[:] = sorted(d for d in dirnames if d != "verifica")
        docs += [os.path.join(dirpath, f) for f in sorted(filenames) if f.endswith(".md") and f not in skip]
    return docs


def exclusions(lines: Iterable[str]) -> list[Ref]:
    """Fatti dichiarati non di business nel caso d'uso: righe "- escluso: file:riga — motivo"."""
    out = []
    for line in lines:
        if re.match(r"^\s*[-*]\s*escluso\s*:", line, re.IGNORECASE):
            out += evidence_of(line)[1]
    return out


def capability_names(path: str) -> set[str]:
    """Nomi delle capability di livello 2: i titoli "### " di capabilities.md (i "## " sono il livello 1)."""
    if not os.path.exists(path):
        return set()
    return {line[4:].strip().lower() for line in read_text(path) if line.startswith("### ")}


@dataclass
class Scenario:
    file: str
    line: int
    name: str
    tags: list[str]


def parse_features(folder: str) -> list[Scenario]:
    """Scenari dei file .feature con i loro tag (anche quelli ereditati dalla Funzionalita')."""
    scenarios = []
    for dirpath, _, filenames in os.walk(folder):
        for name in sorted(f for f in filenames if f.endswith(".feature")):
            path = os.path.join(dirpath, name)
            feature_tags, pending = [], []
            for i, line in enumerate(read_text(path), 1):
                s = line.strip()
                if s.startswith("@"):
                    pending += re.findall(r"@[\w:.-]+", s)
                elif re.match(r"^(Feature|Funzionalit[aà]|Caratteristica)\s*:", s, re.IGNORECASE):
                    feature_tags, pending = pending, []
                elif re.match(r"^(Scenario|Scenario Outline|Schema dello scenario|Esempio|Example)\s*:", s, re.IGNORECASE):
                    scenarios.append(Scenario(path, i, s.split(":", 1)[1].strip(), feature_tags + pending))
                    pending = []
    return scenarios


# ---------------------------------------------------------------------------- valori citati nel testo di una regola

_NUMBER = re.compile(r"(?<![\w.,])(\d{1,3}(?:[.'’ ]\d{3})+|\d+)(?:,(\d+))?(\s*%)?")
_QUOTED = re.compile(r"[\"«“]([^\"»”]{2,60})[\"»”]")
_CODE_NUMBER = re.compile(r"(?<![\w.])\d[\d_]*(?:\.\d+)?")


def literals(text: str) -> list[tuple[str, str, float | str]]:
    """(tipo, testo originale, valore): numeri (anche 10.000 e 0,95), percentuali, testi tra virgolette."""
    out: list[tuple[str, str, float | str]] = []
    plain = RULE_ID.sub(" ", NODE_ID.sub(" ", text))
    for m in _NUMBER.finditer(plain):
        whole = re.sub(r"[.'’ ]", "", m.group(1))
        value = float(whole + ("." + m.group(2) if m.group(2) else ""))
        out.append(("percent" if m.group(3) else "number", m.group(0).strip(), value))
    out += [("text", m.group(1), m.group(1)) for m in _QUOTED.finditer(plain)]
    return out


def numbers_in(code: str) -> set[float]:
    return {float(x.replace("_", "")) for x in _CODE_NUMBER.findall(code)}


def literal_found(kind: str, value: float | str, window: str) -> bool:
    if kind == "text":
        return str(value).lower() in window.lower()
    found = numbers_in(window)
    candidates = {float(value)}
    if kind == "percent":
        p = float(value) / 100
        candidates |= {p, round(1 - p, 10), round(1 + p, 10)}
    return any(abs(c - n) < 1e-9 for c in candidates for n in found)
