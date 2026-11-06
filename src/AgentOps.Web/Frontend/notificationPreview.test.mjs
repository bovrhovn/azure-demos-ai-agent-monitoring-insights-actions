import assert from "node:assert/strict";
import test from "node:test";
import { notificationPreview } from "./notificationPreview.mjs";

test("short and exact-boundary notifications stay intact without a more affordance", () => {
    for (const text of ["", "A short signal", "x".repeat(250), "🚀".repeat(250)]) {
        assert.deepEqual(notificationPreview(text), { text, truncated: false });
    }
    assert.deepEqual(notificationPreview(null), { text: "", truncated: false });
});

test("long messages and raw JSON stop at 250 Unicode code points without altering the source", () => {
    for (const text of ["x".repeat(249) + "🚀tail", '{"message":"<script>unsafe</script>","detail":"' + "long".repeat(10000) + '"}']) {
        const preview = notificationPreview(text);
        assert.equal(preview.truncated, true);
        assert.equal([...preview.text].length, 250);
        assert.equal(preview.text, [...text].slice(0, 250).join(""));
        assert.ok(text.startsWith(preview.text));
    }
});
