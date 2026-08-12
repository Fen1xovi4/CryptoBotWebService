import type { ArbLevel, GfTier, HFLevel, SdLevel, SdTpShift } from './types';

/* Field lists, defaults and units mirror the real bot-creation form in
 * ActiveBotsPage.tsx (AddStrategyModal) so a simulated config matches what a
 * live bot would store in its ConfigJson. Keep these in sync if the bot form
 * changes. Sourced from frontend/src/pages/ActiveBotsPage.tsx ~L4407-L6220. */

export interface MgForm {
  timeframe: string;
  indicatorType: string;
  indicatorLength: string;
  candleCount: string;
  offsetPercent: string;
  takeProfitPercent: string;
  stopLossPercent: string;
}

export interface HfForm {
  takeProfitPercent: string;
  stopLossPercent: string;
  secondsBeforeFunding: string;
  closeAfterMinutes: string;
  maxCycles: string;
  enableLong: boolean;
  minFundingLong: string;
  enableShort: boolean;
  minFundingShort: string;
  // Workspace-level fields, flattened into configJson for simulation only.
  fundingRateMin: string;
  fundingRateMax: string;
}

export interface SdForm {
  timeframe: string;
  direction: string;
  smaPeriod: string;
  takeProfitPercent: string;
  positionSizeUsd: string;
  dcaTriggerBase: string;
  orderType: string;
  entryLimitOffsetPercent: string;
  entryLimitTimeoutBars: string;
  takeProfitTierShiftEnabled: boolean;
}

export interface FcForm {
  maxCycles: string;
  checkBeforeFundingMinutes: string;
  // Workspace-level fields, flattened into configJson for simulation only.
  fcSizeUsdt: string;
  fcMinFundingRatePercent: string;
  fcMaxFundingRatePercent: string;
  fcStopLossPercent: string;
  fcLeverage: string;
  fcSlGraceMinutes: string;
  fcSlCooldownHours: string;
}

export interface GfForm {
  timeframe: string;
  direction: string;
  dcaStepPercent: string;
  tpStepPercent: string;
  leverage: string;
  useStaticRange: boolean;
  takeProfitEnabled: boolean;
  takeProfitTargetUsd: string;
}

export interface GhForm {
  mode: 1 | 2;
  positionMode: 1 | 2;
  secondSymbol: string;
  rangePercent: string;
  upperExitPercent: string;
  dcaStepPercent: string;
  tpStepPercent: string;
  betUsdt: string;
  hedgeNotionalUsdt: string;
  hedgeLeverage: string;
  gridLeverage: string;
}

export interface SghForm {
  lotUsd: string;
  stepPct: string; // display as %, stored as fraction on submit (÷100)
  nUp: string;
  nDown: string;
  skimMode: 0 | 1 | 2;
  leverage: string;
  qHedgeOverride: string; // empty = null (auto)
  autoRestart: boolean;
  makerFeeBps: string;
  takerFeeBps: string;
  takeProfitEnabled: boolean;
  takeProfitTargetUsd: string;
}

export interface ArbForm {
  // Symbol on the second exchange; empty = same symbol as the primary leg.
  secondSymbol: string;
  leverage: string;
  allowBothDirections: boolean;
  maxConsecutiveFailures: string;
}

export interface AllForms {
  mg: MgForm;
  hf: HfForm;
  hfLevels: HFLevel[];
  sd: SdForm;
  sdLevels: SdLevel[];
  sdTpShifts: SdTpShift[];
  fc: FcForm;
  gf: GfForm;
  gfTiers: GfTier[];
  gh: GhForm;
  sgh: SghForm;
  arb: ArbForm;
  arbLevels: ArbLevel[];
}

export const defaultMgForm: MgForm = {
  timeframe: '1h',
  indicatorType: 'EMA',
  indicatorLength: '50',
  candleCount: '50',
  offsetPercent: '0',
  takeProfitPercent: '3',
  stopLossPercent: '3',
};

export const defaultHfForm: HfForm = {
  takeProfitPercent: '1.0',
  stopLossPercent: '0.5',
  secondsBeforeFunding: '10',
  closeAfterMinutes: '10',
  maxCycles: '0',
  enableLong: true,
  minFundingLong: '1.0',
  enableShort: true,
  minFundingShort: '1.0',
  fundingRateMin: '1.0',
  fundingRateMax: '2.0',
};

export const defaultHfLevels: HFLevel[] = [{ offsetPercent: 1.5, sizeUsdt: 50 }];

export const defaultSdForm: SdForm = {
  timeframe: '1h',
  direction: 'Long',
  smaPeriod: '50',
  takeProfitPercent: '1.0',
  positionSizeUsd: '100',
  dcaTriggerBase: 'Average',
  orderType: 'Market',
  entryLimitOffsetPercent: '0.05',
  entryLimitTimeoutBars: '3',
  takeProfitTierShiftEnabled: false,
};

export const defaultSdLevels: SdLevel[] = [{ stepPercent: '3.0', multiplier: '3.0', count: '5' }];
export const defaultSdTpShifts: SdTpShift[] = [{ fromTier: '3', takeProfitPercent: '0.5' }];

export const defaultFcForm: FcForm = {
  maxCycles: '0',
  checkBeforeFundingMinutes: '10',
  fcSizeUsdt: '100',
  fcMinFundingRatePercent: '0.6',
  fcMaxFundingRatePercent: '2.0',
  fcStopLossPercent: '2.5',
  fcLeverage: '3',
  fcSlGraceMinutes: '10',
  fcSlCooldownHours: '6',
};

export const defaultGfForm: GfForm = {
  timeframe: '1h',
  direction: 'Long',
  dcaStepPercent: '1',
  tpStepPercent: '1',
  leverage: '1',
  useStaticRange: false,
  takeProfitEnabled: false,
  takeProfitTargetUsd: '100',
};

export const defaultGfTiers: GfTier[] = [{ upTo: '10', size: '100', dca: '', tp: '' }];

export const defaultGhForm: GhForm = {
  mode: 1,
  positionMode: 1,
  secondSymbol: '',
  rangePercent: '10',
  upperExitPercent: '10',
  dcaStepPercent: '1',
  tpStepPercent: '1',
  betUsdt: '100',
  hedgeNotionalUsdt: '332',
  hedgeLeverage: '5',
  gridLeverage: '1',
};

export const defaultSghForm: SghForm = {
  lotUsd: '50',
  stepPct: '1.0',
  nUp: '10',
  nDown: '10',
  skimMode: 0,
  leverage: '5',
  qHedgeOverride: '',
  autoRestart: true,
  makerFeeBps: '2',
  takerFeeBps: '5.5',
  takeProfitEnabled: false,
  takeProfitTargetUsd: '100',
};

export const defaultArbForm: ArbForm = {
  secondSymbol: '',
  leverage: '1',
  allowBothDirections: true,
  maxConsecutiveFailures: '3',
};

export const defaultArbLevels: ArbLevel[] = [
  { entrySpreadPercent: '1', exitSpreadPercent: '0', notionalUsdt: '100' },
];

export function makeDefaultForms(): AllForms {
  return {
    mg: { ...defaultMgForm },
    hf: { ...defaultHfForm },
    hfLevels: defaultHfLevels.map((l) => ({ ...l })),
    sd: { ...defaultSdForm },
    sdLevels: defaultSdLevels.map((l) => ({ ...l })),
    sdTpShifts: defaultSdTpShifts.map((s) => ({ ...s })),
    fc: { ...defaultFcForm },
    gf: { ...defaultGfForm },
    gfTiers: defaultGfTiers.map((t) => ({ ...t })),
    gh: { ...defaultGhForm },
    sgh: { ...defaultSghForm },
    arb: { ...defaultArbForm },
    arbLevels: defaultArbLevels.map((l) => ({ ...l })),
  };
}
