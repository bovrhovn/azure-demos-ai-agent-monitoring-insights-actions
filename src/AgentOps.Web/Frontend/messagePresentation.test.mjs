import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { inlineParts, presentMessage } from "./messagePresentation.mjs";

const mixedReport = readFileSync(new URL("./messagePresentation.fixture.txt", import.meta.url), "utf8").replace(/\r\n?/g, "\n").trim();

const record = (number, severity = "3") => `${number}) reference: reference-${number}
   timestamp: 2026-11-04T12:38:41.1555793+00:00
   severity: ${severity}
   summary: System.ClientModel.ClientResultException: HTTP 429 — Model deployment rate limit exceeded.`;

test("telemetry lists become grouped records without losing surrounding commentary or exact identifiers", () => {
    const blocks = presentMessage(`Recent results:\n\n${record(1)}\n\n${record(2, "4")}\n\n${record(3, "2")}\n\nTry again later.`);
    assert.deepEqual(blocks.map(block => block.kind), ["paragraph", "telemetry", "paragraph"]);
    assert.deepEqual(blocks[1].records.map(item => [item.reference, item.label, item.tone]), [
        ["reference-1", "Error", "error"], ["reference-2", "Critical", "critical"], ["reference-3", "Warning", "warning"]
    ]);
    assert.equal(blocks[1].records[0].timestamp, "2026-11-04T12:38:41.1555793+00:00");
    assert.match(blocks[1].records[0].summary, /HTTP 429/);
    assert.equal(blocks[2].text, "Try again later.");
    assert.equal(presentMessage(`${record(1)}\n  ${record(2)}`)[0].records.length, 2);
});

test("formatted fields, CRLF and wrapped summaries retain their values", () => {
    const input = record(1).replace("reference: reference-1", "**reference**: `reference-1`")
        .replace("timestamp:", "**timestamp:**").replace("severity: 3", "severity: Error") +
        "\n   Additional provider detail.";
    const [block] = presentMessage(input.replaceAll("\n", "\r\n"));
    assert.equal(block.records[0].reference, "reference-1");
    assert.equal(block.records[0].label, "Error");
    assert.match(block.records[0].summary, /\nAdditional provider detail\.$/);
});

test("unknown severity stays unknown and incomplete or invalid telemetry stays readable", () => {
    assert.equal(presentMessage(record(1, "99"))[0].records[0].label, "Severity 99");
    for (const input of [
        record(1).replace("   severity: 3\n", ""),
        record(1).replace("2026-11-04T12:38:41.1555793+00:00", "not a date")
    ]) {
        const blocks = presentMessage(input);
        assert.ok(blocks.every(block => block.kind !== "telemetry"));
        assert.match(JSON.stringify(blocks), /reference-1/);
        assert.match(JSON.stringify(blocks), /HTTP 429/);
    }
});

test("ordinary replies support headings, paragraphs, ordered and unordered lists, code and tables", () => {
    const blocks = presentMessage("# Findings\n\nA **clear** summary.\n\n3. Retry later\n4. Check quota\n\n- Keep context\n\n```kusto\ntraces | take 3\n```\n\n| Status | Count |\n| --- | ---: |\n| Error | 3 |");
    assert.deepEqual(blocks.map(block => block.kind), ["heading", "paragraph", "list", "list", "code", "table"]);
    assert.equal(blocks[2].start, 3);
    assert.deepEqual(blocks[2].items, ["Retry later", "Check quota"]);
    assert.equal(blocks[3].ordered, false);
    assert.equal(blocks[4].text, "traces | take 3");
    assert.deepEqual(blocks[5].rows, [["Error", "3"]]);
});

test("fenced telemetry remains code and raw HTML or unsafe links never become executable markup", () => {
    assert.equal(presentMessage(`\`\`\`text\n${record(1)}\n\`\`\``)[0].kind, "code");
    const text = '<img src=x onerror=alert(1)> [click](javascript:alert(1))';
    assert.deepEqual(inlineParts(text), [{ tag: "span", text }]);
    assert.deepEqual(inlineParts("**Error** and `429` with *care*").filter(part => part.tag !== "span"), [
        { tag: "strong", text: "Error" }, { tag: "code", text: "429" }, { tag: "em", text: "care" }
    ]);
    assert.deepEqual(inlineParts("**unfinished"), [{ tag: "span", text: "**unfinished" }]);
    for (const text of ["*", "`", "__unfinished"]) assert.deepEqual(inlineParts(text), [{ tag: "span", text }]);
});

const nextSteps = `Suggested next steps (I can run any of these for you):

Implement/verify exponential backoff and retry behavior for 429 responses.
Reduce request concurrency/rate to that deployment, or shard traffic across deployments/regions.
Request higher quota via the Azure docs link in the exception.
I can drill into the operation timeline for this exception to show correlated requests, traces and dependencies (GetOperationTimeline), or fetch recent traces in the same time window. Which would you like me to do?`;

const details = `Query window (UTC): 2026-10-28T12:57:27.7457595 — 2026-11-04T12:57:27.7457595
Exception reference: 99636e6907aa433db77f0d59e034dc6c
Timestamp (UTC): 2026-11-04T12:38:41.1484707
Severity: 3 (Error)
Summary message:
  System.ClientModel.ClientResultException: HTTP 429 (error: rate_limit_exceeded)

  Model deployment rate limit exceeded. To request more quota, see https://learn.microsoft.com/en-us/azure/ai-services/openai/quotas-limits

Top stack frames (most relevant, truncated by telemetry):

level 0: OpenAI, OpenAI.ClientPipelineExtensions+<ProcessMessageAsync>d__0.MoveNext
level 5: OpenAI, OpenAI.Responses.ResponsesClient+<CreateResponseAsync>d__48.MoveNext
level 10: Microsoft.Extensions.AI.OpenAI, Microsoft.Extensions.AI.OpenAIResponsesChatClient+<GetResponseAsync>d__7.MoveNext
level 15: Microsoft.Extensions.AI, Microsoft.Extensions.AI.FunctionInvokingChatClient+<GetResponseAsync>d__38.MoveNext
Notes:

The details payload returned by Application Insights was flagged truncated, so additional stack frames or properties may exist but were not returned.
This is a client-side HTTP 429 from the OpenAI/AI SDK indicating rate limiting.`;

test("plain-language next steps become distinct suggestions with a separate follow-up, not executable actions", () => {
    const [block] = presentMessage(nextSteps);
    assert.equal(block.kind, "nextSteps");
    assert.equal(block.title, "Suggested next steps (I can run any of these for you)");
    assert.equal(block.items.length, 3);
    assert.match(block.items[0], /exponential backoff/);
    assert.match(block.items[1], /shard traffic/);
    assert.match(block.items[2], /higher quota/);
    assert.match(block.followUp[0], /GetOperationTimeline/);
    assert.match(block.followUp[0], /Which would you like me to do\?$/);
});

test("Markdown next steps preserve wrapped suggestions and stop before subsequent content", () => {
    const blocks = presentMessage("## **Next steps**\n\n1. Check quota\n   without changing the deployment.\n2. Retry later\n\nI can show the timeline.\n\n## Findings\n\nNo additional errors.");
    assert.deepEqual(blocks.map(block => block.kind), ["nextSteps", "heading", "paragraph"]);
    assert.deepEqual(blocks[0].items, ["Check quota\nwithout changing the deployment.", "Retry later"]);
    assert.deepEqual(blocks[0].followUp, ["I can show the timeline."]);
    assert.equal(blocks[2].text, "No additional errors.");
});

test("exception details preserve UTC precision, summary paragraphs, stack symbols and truncation notes", () => {
    const blocks = presentMessage(details);
    assert.deepEqual(blocks.map(block => block.kind), ["diagnostic", "summary", "stack", "notes"]);
    assert.deepEqual(blocks[0].fields.map(field => field.label), ["Query window (UTC)", "Exception reference", "Timestamp (UTC)", "Severity"]);
    assert.equal(blocks[0].fields[2].value, "2026-11-04T12:38:41.1484707");
    assert.equal(blocks[0].fields[3].tone, "error");
    assert.equal(blocks[0].fields[3].value, "3 (Error)");
    assert.equal(blocks[1].paragraphs.length, 2);
    assert.match(blocks[1].paragraphs[1], /https:\/\/learn.microsoft.com/);
    assert.deepEqual(blocks[2].frames.map(frame => frame.level), ["0", "5", "10", "15"]);
    assert.match(blocks[2].frames[0].text, /<ProcessMessageAsync>d__0.MoveNext/);
    assert.match(blocks[3].paragraphs.join("\n"), /flagged truncated/);
    assert.match(blocks[3].paragraphs.join("\n"), /client-side HTTP 429/);
});

test("formatted detail fields work without a query window and adjacent next steps stay separate", () => {
    const input = "**Exception reference:** `reference-1`\n\n**Severity**: 4 (Critical)\n\nSummary:\n\nSome detail.\n\n" + nextSteps;
    const blocks = presentMessage(input);
    assert.deepEqual(blocks.map(block => block.kind), ["diagnostic", "summary", "nextSteps"]);
    assert.equal(blocks[0].fields[0].value, "reference-1");
    assert.equal(blocks[0].fields[1].tone, "critical");
    assert.deepEqual(blocks[1].paragraphs, ["Some detail."]);
    assert.equal(presentMessage(`\`\`\`text\n${details}\n\n${nextSteps}\n\`\`\``)[0].kind, "code");
    assert.equal(presentMessage("Exception reference: incomplete")[0].kind, "paragraph");
});

test("documentation URLs become safe links while code, unsafe schemes and HTML remain text", () => {
    const url = "https://learn.microsoft.com/en-us/azure/ai-services/openai/quotas-limits";
    assert.deepEqual(inlineParts(`See ${url}.`), [
        { tag: "span", text: "See " }, { tag: "a", text: url, href: url }, { tag: "span", text: "." }
    ]);
    assert.deepEqual(inlineParts(`[Quota documentation](${url})`), [{ tag: "a", text: "Quota documentation", href: url }]);
    assert.deepEqual(inlineParts(`\`${url}\``), [{ tag: "code", text: url }]);
    assert.deepEqual(inlineParts(`(${url})`).map(part => part.text), ["(", url, ")"]);
    const unsafe = "[Run](javascript:alert(1)) <script>alert(1)</script>";
    assert.deepEqual(inlineParts(unsafe), [{ tag: "span", text: unsafe }]);
});

test("mixed Insights reports structure query windows, exception context and trace entries without inventing evidence", () => {
    const blocks = presentMessage(mixedReport);
    assert.deepEqual(blocks.map(block => block.kind), ["paragraph", "queryWindows", "reportHeading", "report", "report"]);
    assert.deepEqual(blocks[1].windows, [
        { source: "Exceptions", start: "2026-11-05T07:49:55.1444109Z", end: "2026-11-05T08:49:55.1444109Z" },
        { source: "Traces", start: "2026-11-05T07:50:03.818465Z", end: "2026-11-05T08:50:03.818465Z" }
    ]);
    const exception = blocks[3];
    assert.equal(exception.category, "exceptions");
    assert.deepEqual(exception.parts[0].fields, [
        { label: "Count", value: "1" },
        { label: "Reference", value: "2f76713e26bb459c933b9695e478b7af" },
        { label: "Timestamp (UTC)", value: "2026-11-05T08:44:45.6292424Z" }
    ]);
    assert.match(exception.parts.find(part => part.kind === "summary").text, /SocketException.*localhost:8081/);
    assert.match(exception.parts.find(part => part.kind === "details").text, /truncated: true/);
    assert.match(exception.parts.find(part => part.kind === "interpretation").text, /connection was refused/);
    const traces = blocks[4];
    assert.equal(traces.title, "Traces (7 rows, severity >= Warning)");
    assert.equal(traces.parts[0].fields[0].value, "7 (no critical/exceptional traces returned in the window)");
    const entries = traces.parts.find(part => part.kind === "entries").entries;
    assert.equal(entries.length, 4, "only four detailed records were supplied, not seven");
    assert.equal(entries[0].timestamp, "2026-11-05T08:48:46.6107445Z");
    assert.equal(entries[0].reference, "30a1fc987996438cae731614a4d1b574");
    assert.equal(entries[0].summary, '"Event Grid webhook rejected an invalid API key."');
    assert.ok(entries.every(entry => !Object.hasOwn(entry, "severity")), "a minimum severity filter is not a per-record severity");
    assert.match(traces.parts.find(part => part.kind === "paragraph").text, /cfdd615a8da74d53a3fc15b4cdb4165f, d644ba2480594fe4b694aeba3d90973c, adc87c0307414ea4b0d792b78a9811c5/);
    assert.match(traces.parts.find(part => part.kind === "interpretation").text, /missing implementation\/feature-flag/);
});

test("report parsing handles Markdown labels and wrapped context while respecting later sections and fences", () => {
    const formatted = mixedReport.replace("Exceptions (1 row)", "## **Exceptions (1 row)**")
        .replace("Count: 1", "**Count:** 1").replace("Reference: 2f", "**Reference**: 2f")
        .replace("Details: Exception originates", "**Details:** Exception originates\n  More provider context.")
        .replace("Traces (7 rows, severity >= Warning)", "## Traces (7 rows, severity >= Warning)")
        .replace("2026-11-05T08:48:46.6107445Z — ref", "- 2026-11-05T08:48:46.6107445Z — ref");
    const blocks = presentMessage((formatted + "\n\n## Next steps\n\n- Inspect configuration.").replaceAll("\n", "\r\n"));
    assert.equal(blocks.filter(block => block.kind === "report").length, 2);
    assert.match(blocks[3].parts.find(part => part.kind === "details").text, /\n  More provider context\./);
    assert.equal(blocks.at(-1).kind, "nextSteps");
    assert.deepEqual(blocks.at(-1).items, ["Inspect configuration."]);
    assert.equal(presentMessage(`\`\`\`text\n${mixedReport}\n\`\`\``)[0].text, mixedReport);
});

test("unrecognized report content and malformed trace dates remain readable instead of disappearing", () => {
    const blocks = presentMessage("Exceptions (1 row)\nAn unstructured explanation.\n\n## Other\n\nTraces (2 rows)\nCount: 2\nnot-a-date — ref abc — keep this text\n2026-99-05T08:48:46Z — ref def — keep invalid dates\nInterpretation: Review manually.");
    assert.equal(blocks[0].kind, "paragraph");
    assert.match(blocks[0].text, /An unstructured explanation/);
    const report = blocks.find(block => block.kind === "report");
    assert.match(report.parts.find(part => part.kind === "paragraph").text, /not-a-date.*keep this text/);
    assert.match(JSON.stringify(report), /2026-99-05T08:48:46Z/);
    assert.ok(report.parts.every(part => part.kind !== "entries"));
    const invalidWindow = presentMessage("Exceptions: not-a-date — 2026-11-05T08:49:55Z");
    assert.equal(invalidWindow[0].kind, "paragraph");
    assert.match(invalidWindow[0].text, /not-a-date/);
});
