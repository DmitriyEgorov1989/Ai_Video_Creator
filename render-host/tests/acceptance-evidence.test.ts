import assert from "node:assert/strict";
import {createRequire} from "node:module";
import {readFile} from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import {fileURLToPath} from "node:url";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
type Validate = ((value: unknown) => boolean) & {errors?: unknown};
type AjvConstructor = new (options: {allErrors: boolean}) => {compile: (schema: unknown) => Validate};
const Ajv2020 = createRequire(import.meta.url)("ajv/dist/2020.js").default as AjvConstructor;
const schema = JSON.parse(
  await readFile(path.join(root, "contracts", "acceptance-evidence.v2.schema.json"), "utf8"),
);
const validate = new Ajv2020({allErrors: true}).compile(schema);
const hash = "a".repeat(64);
const validEvidence = {
  schemaVersion: 2,
  jobId: "1".repeat(32),
  outcome: "Passed",
  variantId: "variant",
  approvedRevisionId: "revision",
  planHash: hash,
  recomputedPlanHash: hash,
  auditHash: hash,
  recomputedAuditHash: hash,
  skillIds: ["multi-clip-director"],
  sourceCount: 1,
  sourceMode: "single-source",
  successfulDeliveryCount: 1,
  uploadedSha256: hash,
  uploadedSizeBytes: 123,
  actualCostUsd: 0.01,
  artifacts: [{
    artifactType: "final-mp4",
    artifactId: "artifact",
    storedSha256: hash,
    storedSizeBytes: 123,
    exists: true,
    actualSha256: hash,
    actualSizeBytes: 123,
  }],
  checks: [{code: "job.completed", passed: true}],
};

test("acceptance evidence V2 schema accepts the complete report", () => {
  assert.equal(validate(validEvidence), true, JSON.stringify(validate.errors));
});

test("acceptance evidence V2 schema rejects incomplete or malformed upload evidence", () => {
  const {uploadedSha256: _, ...missingHash} = validEvidence;
  assert.equal(validate(missingHash), false);
  assert.equal(validate({...validEvidence, uploadedSha256: "invalid"}), false);
  assert.equal(validate({...validEvidence, secret: "must-not-be-accepted"}), false);
});
