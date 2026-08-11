import { entity, role, text, date, uuid } from '@microsoft/rayfin-core';

/**
 * Rollup of AI router traffic, mirrored from mimamori.AiRequestLogs.
 *
 * 見守り隊 talks to every language model through OrcaRouter's OpenAI-compatible
 * endpoint, so this table is the evidence that the routing actually happens:
 * one row per (purpose, router, resolvedModel) with counts and a mean latency.
 *
 * Counts only -- never a prompt, never a completion, never a household id.
 * The source table stores no prompt text either, but aggregating here keeps the
 * console's grain identical to what the charts draw.
 *
 * Reading the rows:
 *  - `router` is the X-Orca-Router response header. "auto" means OrcaRouter's
 *    own router picked the model; "OrcaRouter" means the header was absent
 *    because the caller pinned a model. Both went through OrcaRouter.
 *  - "MockAiRouter" is the offline stub used before an API key is configured,
 *    and is the one value that did NOT go through OrcaRouter.
 */
@entity()
@role('authenticated', 'read')
export class AiRouterCall {
  @uuid() id!: string;

  /** Call site: "intent" | "intent-repair" | "summary" | "summary-fast" | "conversation" | "alert-message". */
  @text({ max: 64 }) purpose!: string;

  /** "auto" (OrcaRouter chose), "OrcaRouter" (model pinned by us), or "MockAiRouter". */
  @text({ max: 64 }) router!: string;

  /** The model that actually served the request, e.g. "deepseek-v4-pro". */
  @text({ max: 128 }) resolvedModel!: string;

  /** Requests in this group. */
  @text({ max: 10 }) callCount!: string;

  /** Subset of `callCount` that returned a usable completion. */
  @text({ max: 10 }) successCount!: string;

  /** Mean end-to-end latency in milliseconds -- the number behind the model-pinning decision. */
  @text({ max: 12 }) avgDurationMs!: string;

  /** Most recent call in this group. */
  @date() lastCalledAt!: Date;
}
