import { entity, role, text, boolean, date, uuid } from '@microsoft/rayfin-core';

/**
 * One alert delivery attempt mirrored from the 見守り隊 application's WatchAlert
 * table, so an operator can see *why* a household is flagged without opening the
 * main app.
 *
 * Deliberately excluded: the alert's `Message` body. That text is written for
 * the family and can name the resident and describe their behaviour, which has
 * no operational value here. Only the machine-generated `reason`, the risk
 * level/score and the delivery outcome are mirrored.
 */
@entity()
@role('authenticated', 'read')
export class AlertRecord {
  @uuid() id!: string;

  /** Household.Id in the 見守り隊 application database. */
  @text() householdId!: string;

  @text({ min: 1, max: 200 }) householdName!: string;

  /** "Low" | "Medium" | "High". */
  @text({ max: 10 }) riskLevel!: string;

  @text({ max: 10 }) score!: string;

  /** Machine-generated rule name, e.g. "長時間の無反応". */
  @text({ max: 300 }) reason!: string;

  /** False when the LINE push failed; those are what an operator chases. */
  @boolean() success!: boolean;

  /** Delivery failure summary. Empty on success. */
  @text({ max: 500 }) error!: string;

  @date() sentAt!: Date;
}
