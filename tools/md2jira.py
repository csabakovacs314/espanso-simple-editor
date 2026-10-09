#!/usr/bin/env python3
"""Markdown -> Jira wiki markup, for the subset the JOSIP and Espanso Simple Editor docs use.

Taken from JOSIP (tools/md2jira.py). Added for Espanso Simple Editor:
  * images: ![alt](images/x.png) -> !x.png|width=900! (attach the PNG to the issue first)
  * quote boxes: a "> " block whose first line is **In short** becomes {panel}, **Tip:** becomes
    {tip}, **Warning:** becomes {warning}, anything else {quote}
  * a table of contents ({toc}) right after the first panel, when the page has four or more sections
  * a backslash at the end of `code` is dropped, because Jira reads it as escaping the closing braces


Two passes. The first REFLOWS the source: markdown treats a single newline inside a
paragraph or list item as a space, Jira treats it as a hard line break, and inline
markup like **bold** can straddle one. Joining those lines first means the second
pass only ever sees whole logical lines, so nothing is half-converted.
"""
import re, sys

FENCE   = re.compile(r'^```')
HEADING = re.compile(r'^#{1,6}\s')
RULE    = re.compile(r'^\s*---+\s*$')
QUOTE   = re.compile(r'^\s*>')
IMAGE   = re.compile(r'!\[([^\]]*)\]\(([^)\s]+)\)')
BULLET  = re.compile(r'^(\s*)[-*]\s+(.*)$')
NUMBER  = re.compile(r'^(\s*)\d+\.\s+(.*)$')

def is_block_start(ln):
    return (not ln.strip() or FENCE.match(ln) or HEADING.match(ln) or RULE.match(ln)
            or ln.strip().startswith("|") or BULLET.match(ln) or NUMBER.match(ln)
            or QUOTE.match(ln) or IMAGE.match(ln.strip()))

def reflow(md):
    out, in_code = [], False
    for ln in md.split("\n"):
        if FENCE.match(ln):
            in_code = not in_code; out.append(ln); continue
        if in_code:
            out.append(ln); continue
        # A bullet or numbered item IS a valid join target: its wrapped continuation
        # lines belong to it. Only headings, rules, tables, fences and blank lines
        # end a logical line.
        prev_open = bool(out) and out[-1].strip() and not (
            FENCE.match(out[-1]) or HEADING.match(out[-1]) or RULE.match(out[-1])
            or out[-1].strip().startswith("|") or QUOTE.match(out[-1]) or IMAGE.match(out[-1].strip()))
        if prev_open and ln.strip() and not is_block_start(ln):
            out[-1] = out[-1].rstrip() + " " + ln.strip()
        else:
            out.append(ln)
    return out

def inline(t):
    spans = []
    def stash(m):
        spans.append(m.group(1)); return f"\x00{len(spans)-1}\x00"
    t = re.sub(r'`([^`]+)`', stash, t)
    spans[:] = [c.rstrip('\\') for c in spans]
    t = IMAGE.sub(lambda m: '!' + m.group(2).replace('\\', '/').split('/')[-1] + '|width=900!', t)
    t = re.sub(r'\*\*(.+?)\*\*', r'*\1*', t)
    t = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'[\1|\2]', t)
    t = re.sub(r'\x00(\d+)\x00', lambda m: '{{' + spans[int(m.group(1))] + '}}', t)
    return t

def convert(md):
    lines, out, i, fence = reflow(md), [], 0, None
    while i < len(lines):
        ln = lines[i]
        m = re.match(r'^```(\w*)\s*$', ln)
        if m:
            if fence is None:
                lang = m.group(1)
                fence = lang or "none"
                out.append("{code:" + lang + "}" if lang else "{noformat}")
            else:
                out.append("{noformat}" if fence == "none" else "{code}")
                fence = None
            i += 1; continue
        if fence is not None:
            out.append(ln); i += 1; continue

        if RULE.match(ln):
            out.append("----"); i += 1; continue

        if QUOTE.match(ln):
            body = []
            while i < len(lines) and QUOTE.match(lines[i]):
                body.append(re.sub(r'^\s*>\s?', '', lines[i])); i += 1
            first = body[0].strip() if body else ""
            m2 = re.match(r'^\*\*(In short|Tip:|Warning:)\*\*\s*(.*)$', first)
            kind = {"In short": "panel", "Tip:": "tip", "Warning:": "warning"}.get(m2.group(1)) if m2 else None
            if kind:
                body[0] = m2.group(2)
                title = "In short" if kind == "panel" else m2.group(1).rstrip(":")
                out.append("{panel:title=In short|bgColor=#F4F5F7}" if kind == "panel" else "{" + kind + ":title=" + title + "}")
            else:
                out.append("{quote}")
            out.extend(inline(b) for b in body if b.strip())
            out.append("{" + (kind or "quote") + "}")
            if kind == "panel" and not any(o.startswith("{toc") for o in out) \
                    and sum(1 for l in lines if l.startswith("## ")) >= 4:
                out.extend(["", "{toc:maxLevel=2}"])
            continue

        h = re.match(r'^(#{1,6})\s+(.*)$', ln)
        if h:
            out.append(f"h{len(h.group(1))}. {inline(h.group(2))}"); i += 1; continue

        if ln.strip().startswith("|") and i + 1 < len(lines) and re.match(r'^\s*\|[\s:|-]+\|\s*$', lines[i+1]):
            cells = [c.strip() for c in ln.strip().strip("|").split("|")]
            # An empty header row means a borderless layout table; emitting it would
            # render as a visible blank header in Jira.
            if any(cells):
                out.append("||" + "||".join(inline(c) for c in cells) + "||")
            i += 2
            while i < len(lines) and lines[i].strip().startswith("|"):
                cells = [c.strip() for c in lines[i].strip().strip("|").split("|")]
                out.append("|" + "|".join(inline(c) or " " for c in cells) + "|")
                i += 1
            continue

        b = BULLET.match(ln)
        if b:
            out.append("*" * (len(b.group(1)) // 2 + 1) + " " + inline(b.group(2))); i += 1; continue

        n = NUMBER.match(ln)
        if n:
            out.append("#" * (len(n.group(1)) // 2 + 1) + " " + inline(n.group(2))); i += 1; continue

        out.append(inline(ln)); i += 1
    return "\n".join(out)

if __name__ == "__main__":
    for path in sys.argv[1:]:
        dst = path.rsplit(".", 1)[0] + ".jira.txt"
        open(dst, "w", encoding="utf-8").write(convert(open(path, encoding="utf-8").read()))
        print("wrote", dst)
