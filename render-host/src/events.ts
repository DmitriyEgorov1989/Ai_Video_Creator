import type { RenderEvent } from "./contracts.js";

export function emit(event: RenderEvent): void {
  process.stdout.write(`${JSON.stringify(event)}\n`);
}
