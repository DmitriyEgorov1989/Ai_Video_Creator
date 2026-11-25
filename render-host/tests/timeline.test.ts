import assert from "node:assert/strict";
import test from "node:test";
import { sourceTimestampToCompositionFrame } from "../src/templates/DynamicComposition.js";

test("source timestamps use composition frames rather than source frames", () => {
  assert.equal(sourceTimestampToCompositionFrame(2_000_000, 30), 60);
});
