const severityNames = ["Verbose", "Information", "Warning", "Error", "Critical"];
const listItem = /^\s*(?:(\d+)[.)]|[-*+])\s+(.+)$/;
const heading = /^#{1,6}\s+(.+)$/;
const fence = /^\s*(`{3,}|~{3,})(.*)$/;
const field = /^\s*(?:[-*]\s+)?(?:\*\*)?(reference|timestamp|severity|summary)(?:\*\*)?\s*:(?:\*\*)?\s*(.*?)\s*$/i;
const unquote = value => value.replace(/^`([^`]+)`$/, "$1");
const cells = line => line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map(cell => cell.trim());
const tableDivider = line => line.includes("|") && cells(line).every(cell => /^:?-{3,}:?$/.test(cell));
const diagnosticField = /^(?:\*\*)?(query window(?: \(UTC\))?|exception reference|timestamp(?: \(UTC\))?|severity)(?:\*\*)?\s*:(?:\*\*)?\s*(.+)$/i;

export function inlineParts(text) {
    const parts = [];
    const pattern = /`[^`\n]+`|\*\*[^*\n]+\*\*|__[^_\n]+__|\*[^*\n]+\*|\[[^\]\n]+\]\(https?:\/\/[^\s<>]+\)|https?:\/\/[^\s<>`]+/g;
    let end = 0;
    for (const match of text.matchAll(pattern)) {
        if (match.index > end) parts.push({ tag: "span", text: text.slice(end, match.index) });
        const part = match[0];
        if (part.startsWith("`")) parts.push({ tag: "code", text: part.slice(1, -1) });
        else if (/^(\*\*|__)/.test(part)) parts.push({ tag: "strong", text: part.slice(2, -2) });
        else if (part.startsWith("*")) parts.push({ tag: "em", text: part.slice(1, -1) });
        else {
            const link = part.match(/^\[([^\]]+)\]\((.+)\)$/);
            let href = link?.[2] ?? part.replace(/[.,;:!?]+$/, "");
            if (!link) {
                while (href.endsWith(")") && href.split(")").length > href.split("(").length) href = href.slice(0, -1);
            }
            if (URL.canParse(href) && ["http:", "https:"].includes(new URL(href).protocol)) {
                parts.push({ tag: "a", text: link?.[1] ?? href, href });
                if (!link && href.length < part.length) parts.push({ tag: "span", text: part.slice(href.length) });
            } else parts.push({ tag: "span", text: part });
        }
        end = match.index + part.length;
    }
    if (end < text.length) parts.push({ tag: "span", text: text.slice(end) });
    return parts;
}

function severityInfo(value) {
    const numeric = value.match(/^(\d)(?:\s*\([^)]+\))?$/);
    const level = numeric ? severityNames[Number(numeric[1])] :
        severityNames.find(name => name.toLowerCase() === value.toLowerCase());
    return { label: level ?? `Severity ${value}`, tone: level?.toLowerCase() ?? "unknown" };
}

function sectionAt(line) {
    const title = line.trim().replace(/^#{1,6}\s+/, "").replace(/\*\*/g, "").replace(/:\s*$/, "");
    if (/^(?:(?:suggested|recommended)\s+)?next steps(?:\s*\([^)]*\))?$/i.test(title)) return { kind: "nextSteps", title };
    if (/^summary(?: message)?$/i.test(title)) return { kind: "summary", title };
    if (/^(?:top )?stack (?:frames|trace)(?:\s*\([^)]*\))?$/i.test(title)) return { kind: "stack", title };
    if (/^notes$/i.test(title)) return { kind: "notes", title };
    return null;
}

function diagnosticAt(lines, start) {
    const fields = [];
    let end = start;
    while (end < lines.length) {
        const match = lines[end].trim().match(diagnosticField);
        if (!match) {
            if (!lines[end].trim() && lines[end + 1]?.trim().match(diagnosticField)) { end++; continue; }
            break;
        }
        const value = unquote(match[2]);
        fields.push({ label: match[1], value, ...(match[1].toLowerCase() === "severity" ? { tone: severityInfo(value).tone } : {}) });
        end++;
    }
    if (fields.length < 2 || !fields.some(item => /^exception reference$/i.test(item.label))) return null;
    return { end, block: { kind: "diagnostic", fields } };
}

function sectionBody(lines, start, section) {
    const body = [];
    let end = start + 1;
    while (end < lines.length && !sectionAt(lines[end]) && !lines[end].match(heading) &&
        !lines[end].match(fence) && !diagnosticAt(lines, end) && !telemetryAt(lines, end)) {
        if (lines[end].includes("|") && lines[end + 1] && tableDivider(lines[end + 1])) break;
        body.push(lines[end++]);
    }
    const paragraphs = body.join("\n").trim().split(/\n\s*\n/).filter(Boolean);
    if (section.kind === "nextSteps") {
        const items = [];
        const followUp = [];
        let following = false;
        for (const paragraph of paragraphs) {
            for (const line of paragraph.split("\n").filter(line => line.trim())) {
                const item = line.match(listItem);
                if (!item && /^(?:I can\b|Which\b|Would you\b|Let me\b|Tell me\b)/i.test(line.trim())) following = true;
                if (following) followUp.push(line.trim());
                else if (/^\s+\S/.test(line) && !item && items.length) items[items.length - 1] += `\n${line.trim()}`;
                else items.push(item?.[2] ?? line.trim());
            }
        }
        return { end, block: { ...section, items, followUp } };
    }
    if (section.kind === "stack") {
        const frames = [];
        for (const line of body.filter(line => line.trim())) {
            const match = line.match(/^\s*(?:[-*]\s+|\d+[.)]\s+)?level\s+(\d+)\s*:\s*(.+)$/i);
            if (!match && /^\s+\S/.test(line) && frames.length) frames.at(-1).text += `\n${line.trim()}`;
            else frames.push({ level: match?.[1] ?? null, text: match?.[2] ?? line.trim() });
        }
        return { end, block: { ...section, frames } };
    }
    return { end, block: { ...section, paragraphs: paragraphs.map(text => text.trim()) } };
}

function telemetryAt(lines, start) {
    const first = lines[start].replace(listItem, "$2");
    if (!/^reference$/i.test(first.match(field)?.[1] ?? "")) return null;
    const values = {};
    let end = start;
    let lastField;
    for (; end < lines.length; end++) {
        const line = end === start ? first : lines[end];
        const match = line.match(field);
        if (match) {
            const name = match[1].toLowerCase();
            if (Object.hasOwn(values, name)) break;
            values[name] = unquote(match[2]);
            lastField = name;
        } else if (line.match(listItem)) {
            break;
        } else if (line.trim() && /^\s+/.test(line) && lastField === "summary") {
            values.summary += `\n${line.trim()}`;
        } else if (!line.trim() && !values.summary && lines[end + 1]?.match(field)) {
            continue;
        } else break;
    }
    if (!values.reference || !values.summary || !values.timestamp || values.severity === undefined ||
        !/^\d{4}-\d\d-\d\dT/.test(values.timestamp) || Number.isNaN(Date.parse(values.timestamp))) return null;
    return {
        end,
        record: { ...values, ...severityInfo(values.severity) }
    };
}

export function presentMessage(content) {
    const lines = content.replace(/\r\n?/g, "\n").split("\n");
    const blocks = [];
    for (let index = 0; index < lines.length;) {
        const line = lines[index];
        if (!line.trim()) { index++; continue; }
        const code = line.match(fence);
        if (code) {
            const body = [];
            index++;
            while (index < lines.length) {
                const closing = lines[index].match(fence);
                if (closing && closing[1][0] === code[1][0] && closing[1].length >= code[1].length && !closing[2].trim()) {
                    index++;
                    break;
                }
                body.push(lines[index++]);
            }
            blocks.push({ kind: "code", language: code[2].trim(), text: body.join("\n") });
            continue;
        }
        const diagnostic = diagnosticAt(lines, index);
        if (diagnostic) {
            blocks.push(diagnostic.block);
            index = diagnostic.end;
            continue;
        }
        const section = sectionAt(line);
        if (section) {
            const result = sectionBody(lines, index, section);
            blocks.push(result.block);
            index = result.end;
            continue;
        }
        const telemetry = telemetryAt(lines, index);
        if (telemetry) {
            const previous = blocks.at(-1);
            if (previous?.kind === "telemetry") previous.records.push(telemetry.record);
            else blocks.push({ kind: "telemetry", records: [telemetry.record] });
            index = telemetry.end;
            continue;
        }
        const title = line.match(heading);
        if (title) {
            blocks.push({ kind: "heading", text: title[1] });
            index++;
            continue;
        }
        if (index + 1 < lines.length && line.includes("|") && tableDivider(lines[index + 1])) {
            const headers = cells(line);
            const rows = [];
            index += 2;
            while (index < lines.length && lines[index].includes("|") && lines[index].trim()) rows.push(cells(lines[index++]));
            blocks.push({ kind: "table", headers, rows });
            continue;
        }
        const item = line.match(listItem);
        if (item) {
            const ordered = Boolean(item[1]);
            const items = [];
            const start = ordered ? Number(item[1]) : undefined;
            while (index < lines.length) {
                const next = lines[index].match(listItem);
                if (!next || Boolean(next[1]) !== ordered || telemetryAt(lines, index)) break;
                items.push(next[2]);
                index++;
                while (index < lines.length && /^\s+\S/.test(lines[index]) && !lines[index].match(listItem)) {
                    items[items.length - 1] += `\n${lines[index++].trim()}`;
                }
            }
            blocks.push({ kind: "list", ordered, start, items });
            continue;
        }
        const paragraph = [line];
        index++;
        while (index < lines.length && lines[index].trim() && !lines[index].match(heading) &&
            !lines[index].match(listItem) && !lines[index].match(fence) && !telemetryAt(lines, index) &&
            !sectionAt(lines[index]) && !diagnosticAt(lines, index) &&
            !(lines[index].includes("|") && lines[index + 1] && tableDivider(lines[index + 1]))) {
            paragraph.push(lines[index++]);
        }
        blocks.push({ kind: "paragraph", text: paragraph.join("\n") });
    }
    return blocks;
}
