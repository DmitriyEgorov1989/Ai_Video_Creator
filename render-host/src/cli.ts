#!/usr/bin/env node
import { readRenderRequest, renderRequest, RenderHostError } from "./render.js";
import { emit } from "./events.js";
import { templates } from "./templates.js";
import { validateRenderRequest } from "./validate.js";

async function main(args: string[]): Promise<number> {
  const command = args[0];
  if (command === "list-templates") {
    process.stdout.write(`${JSON.stringify({ templates })}\n`);
    return 0;
  }

  if (command !== "validate" && command !== "render") {
    process.stderr.write("Usage: render-host <list-templates|validate|render> [--job <request.json>]\n");
    return 2;
  }

  const jobIndex = args.indexOf("--job");
  const jobPath = jobIndex >= 0 ? args[jobIndex + 1] : undefined;
  if (!jobPath) {
    process.stderr.write("Missing required --job <request.json>.\n");
    return 2;
  }

  try {
    const request = await readRenderRequest(jobPath);
    const issues = validateRenderRequest(request);
    if (issues.length > 0) {
      throw new RenderHostError("request_invalid", issues.map((item) => `${item.code} at ${item.path}: ${item.message}`).join("; "));
    }
    if (command === "validate") {
      emit({ type: "completed", outputPath: request.outputPath, durationFrames: request.plan.output.durationFrames });
      return 0;
    }
    await renderRequest(request);
    return 0;
  } catch (error) {
    const code = error instanceof RenderHostError ? error.code : "host_error";
    const message = error instanceof Error ? error.message : String(error);
    emit({ type: "failed", code, message });
    process.stderr.write(`${code}: ${message}\n`);
    return 1;
  }
}

main(process.argv.slice(2)).then((code) => {
  process.exitCode = code;
});
