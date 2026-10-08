/** The queue row exactly as the API returns it (camelCase over the wire). */
export interface WorklistRow {
  claimId: string;
  payerId: string;
  payerName: string;
  category: string;
  team: string;
  /** null = the expert sample has no position on this category. Not the same as "false". */
  preventableAtPrebill: boolean | null;
  coveredByLabeledSample: boolean;
  nextAction: string;
  bucket: "RECOVERABLE" | "POLICY_BLOCKED" | "EXPIRED";
  anyDenialRouteOpen: boolean;
  denialDate: string;
  appealEnds: string;
  daysRemaining: number | null;
  charge: number;
  status: string;
  assignee: string | null;
  lastNote: string | null;
  confidence: number;
  requiresHumanReview: boolean;
  confidenceFactors: string[];
  priority: number;
  priorityExplain: string;
  hasDraft: boolean;
  draftBody: string | null;
  draftVerdict: string | null;
  draftReason: string | null;
  draftAt: string | null;
}

export interface WorkEvent {
  at: string;
  actor: string;
  field: string;
  before: string | null;
  after: string | null;
  note: string | null;
}

export interface GroupCount {
  status?: string;
  bucket?: string;
  count: number;
  amount: number;
}

export interface WorklistResponse {
  role: string;
  you: string;
  total: number;
  needsReview: number;
  moneyAtStake: number;
  byStatus: GroupCount[];
  byBucket: GroupCount[];
  rows: WorklistRow[];
}

export interface Adjustment {
  groupCode: string;
  carc: string;
  amount: number;
  scope: string;
}

export interface HistoryEntry {
  seq: number;
  payerId: string;
  checkDate: string;
  statusCode: string;
  submittedCharge: number;
  paidAmount: number;
  patientResponsibility: number;
  sourceFile: string | null;
  adjustments: Adjustment[];
}

export interface ClaimLine {
  lineNo: number;
  cpt: string;
  modifier: string;
  units: number;
  charge: number;
  dx1: string;
  dx2: string;
  dx3: string;
  dx4: string;
}

export interface ClaimDetail {
  claimId: string;
  payerId: string;
  payerName: string;
  dos: string;
  submittedDate: string;
  charge: number;
  adjudicated: boolean;
  currentStatus: string;
  paidAmount: number;
  lifetimePaid: number;
  lastCheckDate: string | null;
  lines: ClaimLine[];
  history: HistoryEntry[];
}

export interface ItemDetail {
  row: WorklistRow;
  events: WorkEvent[];
  claim: ClaimDetail | null;
}

export interface Analytics {
  moneyAtRisk: {
    openCount: number;
    openAmount: number;
    buckets: { bucket: string; count: number; amount: number }[];
    pendingCount: number;
    pendingAmount: number;
    zeroPaidLineCount: number;
    zeroPaidLineAmount: number;
  };
  byPayer: Dimension[];
  byCategory: Dimension[];
  byProvider: Dimension[];
  byCoder: Dimension[];
  byFacility: Dimension[];
  byPreBill: { reviewed: string; count: number; denials: number; denialRate: number }[];
  trend: { period: string; count: number; amount: number }[];
}

export interface Dimension {
  key: string;
  count: number;
  amount: number;
}

export interface PreventionReport {
  generatedAt: string;
  evaluatedClaims: number;
  checks: PreventionCheck[];
}

export interface PreventionCheck {
  id: string;
  name: string;
  question: string;
  /** False when the pack has no field the check needs — the number is then absent, not zero. */
  measurable: boolean;
  whyNotMeasured: string | null;
  claimsScreened: number;
  /** Burden: what the check would have held up for review. */
  claimsFlagged: number;
  /** Benefit: open denials the check would have stopped. */
  denialsCaught: number;
  amountCaught: number;
  openDenialsTotal: number;
  openDenialAmountTotal: number;
  catchRate: number;
  burdenRate: number;
  confidence: "observed" | "association" | "not_measurable";
  /** Machine-readable form a pre-bill system can apply. */
  rule: Record<string, string>;
}
