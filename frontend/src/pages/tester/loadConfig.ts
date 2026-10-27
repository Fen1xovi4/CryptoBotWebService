/** Inverse of buildConfig.ts: fills the Tester form state from a strategy configJson (a saved
 * parameter set or a live bot's config), so the parameters are visible and editable in the form
 * before re-running them on fresh history. Unknown/missing fields keep their current form values. */

import type { AllForms, GfForm, GhForm, HfForm, MgForm, SdForm, SghForm } from './formDefaults';
import type { StrategyType } from './types';

type Json = Record<string, unknown>;

const str = (v: unknown, fallback: string): string =>
  typeof v === 'number' && Number.isFinite(v) ? String(v) : typeof v === 'string' && v !== '' ? v : fallback;
const bool = (v: unknown, fallback: boolean): boolean => (typeof v === 'boolean' ? v : fallback);
const optStr = (v: unknown): string => (typeof v === 'number' && Number.isFinite(v) ? String(v) : '');

export function configToForms(strategyType: StrategyType, configJson: string, forms: AllForms): AllForms {
  let c: Json;
  try {
    c = JSON.parse(configJson) as Json;
  } catch {
    throw new Error('Сохранённый конфиг не является корректным JSON');
  }
  if (!c || typeof c !== 'object') throw new Error('Сохранённый конфиг пуст');

  switch (strategyType) {
    case 'HuntingFunding': {
      const hf: HfForm = {
        ...forms.hf,
        takeProfitPercent: str(c.takeProfitPercent, forms.hf.takeProfitPercent),
        stopLossPercent: str(c.stopLossPercent, forms.hf.stopLossPercent),
        secondsBeforeFunding: str(c.secondsBeforeFunding, forms.hf.secondsBeforeFunding),
        closeAfterMinutes: str(c.closeAfterMinutes, forms.hf.closeAfterMinutes),
        maxCycles: str(c.maxCycles, forms.hf.maxCycles),
        enableLong: bool(c.enableLong, forms.hf.enableLong),
        minFundingLong: str(c.minFundingLong, forms.hf.minFundingLong),
        enableShort: bool(c.enableShort, forms.hf.enableShort),
        minFundingShort: str(c.minFundingShort, forms.hf.minFundingShort),
        fundingRateMin: str(c.fundingRateMin, forms.hf.fundingRateMin),
        fundingRateMax: str(c.fundingRateMax, forms.hf.fundingRateMax),
      };
      const levels = Array.isArray(c.levels)
        ? (c.levels as Json[]).map((l) => ({ offsetPercent: Number(l.offsetPercent ?? 0), sizeUsdt: Number(l.sizeUsdt ?? 0) }))
        : forms.hfLevels;
      return { ...forms, hf, hfLevels: levels.length ? levels : forms.hfLevels };
    }

    case 'SmaDca': {
      const sd: SdForm = {
        ...forms.sd,
        timeframe: str(c.timeframe, forms.sd.timeframe),
        direction: str(c.direction, forms.sd.direction),
        smaPeriod: str(c.smaPeriod, forms.sd.smaPeriod),
        takeProfitPercent: str(c.takeProfitPercent, forms.sd.takeProfitPercent),
        positionSizeUsd: str(c.positionSizeUsd, forms.sd.positionSizeUsd),
        dcaTriggerBase: str(c.dcaTriggerBase, forms.sd.dcaTriggerBase),
        orderType: str(c.orderType, forms.sd.orderType),
        entryLimitOffsetPercent: str(c.entryLimitOffsetPercent, forms.sd.entryLimitOffsetPercent),
        entryLimitTimeoutBars: str(c.entryLimitTimeoutBars, forms.sd.entryLimitTimeoutBars),
        takeProfitTierShiftEnabled: bool(c.takeProfitTierShiftEnabled, forms.sd.takeProfitTierShiftEnabled),
      };
      const levels = Array.isArray(c.levels)
        ? (c.levels as Json[]).map((l) => ({ stepPercent: str(l.stepPercent, '0'), multiplier: str(l.multiplier, '1'), count: str(l.count, '1') }))
        : forms.sdLevels;
      const shifts = Array.isArray(c.takeProfitTierShifts) && (c.takeProfitTierShifts as Json[]).length
        ? (c.takeProfitTierShifts as Json[]).map((s) => ({ fromTier: str(s.fromTier, '1'), takeProfitPercent: str(s.takeProfitPercent, '0.5') }))
        : forms.sdTpShifts;
      return { ...forms, sd, sdLevels: levels.length ? levels : forms.sdLevels, sdTpShifts: shifts };
    }

    case 'GridFloat': {
      const restart = bool(c.restartOnRangeExit, forms.gf.restartOnRangeExit);
      const gf: GfForm = {
        ...forms.gf,
        timeframe: str(c.timeframe, forms.gf.timeframe),
        direction: str(c.direction, forms.gf.direction),
        dcaStepPercent: str(c.dcaStepPercent, forms.gf.dcaStepPercent),
        tpStepPercent: str(c.tpStepPercent, forms.gf.tpStepPercent),
        leverage: str(c.leverage, forms.gf.leverage),
        useStaticRange: bool(c.useStaticRange, forms.gf.useStaticRange),
        takeProfitEnabled: bool(c.takeProfitEnabled, forms.gf.takeProfitEnabled),
        takeProfitTargetUsd: str(c.takeProfitTargetUsd, forms.gf.takeProfitTargetUsd),
        restartOnRangeExit: restart,
        stakeMultiplier: str(c.stakeMultiplier, forms.gf.stakeMultiplier),
        maxStakeStages: str(c.maxStakeStages, forms.gf.maxStakeStages),
        takeProfitContinue: bool(c.takeProfitContinue, forms.gf.takeProfitContinue),
        anchorPriceLimit: c.anchorPriceLimit == null ? '' : optStr(c.anchorPriceLimit),
      };
      const tiers = Array.isArray(c.tiers)
        ? (c.tiers as Json[]).map((t) => ({
            upTo: str(t.upToPercent, '0'),
            size: str(t.sizeUsdt, '0'),
            dca: optStr(t.dcaStepPercent),
            tp: optStr(t.tpStepPercent),
          }))
        : forms.gfTiers;
      return { ...forms, gf, gfTiers: tiers.length ? tiers : forms.gfTiers };
    }

    case 'GridHedge': {
      const mode = c.mode === 2 ? 2 : c.mode === 1 ? 1 : forms.gh.mode;
      const gh: GhForm = {
        ...forms.gh,
        mode,
        positionMode: c.positionMode === 2 ? 2 : c.positionMode === 1 ? 1 : forms.gh.positionMode,
        secondSymbol: typeof c.hedgeSymbol === 'string' && mode === 2 ? c.hedgeSymbol : forms.gh.secondSymbol,
        rangePercent: str(c.rangePercent, forms.gh.rangePercent),
        upperExitPercent: str(c.upperExitPercent, forms.gh.upperExitPercent),
        dcaStepPercent: str(c.dcaStepPercent, forms.gh.dcaStepPercent),
        tpStepPercent: str(c.tpStepPercent, forms.gh.tpStepPercent),
        betUsdt: str(c.betUsdt, forms.gh.betUsdt),
        hedgeNotionalUsdt: str(c.hedgeNotionalUsdt, forms.gh.hedgeNotionalUsdt),
        hedgeLeverage: str(c.hedgeLeverage, forms.gh.hedgeLeverage),
        gridLeverage: str(c.gridLeverage, forms.gh.gridLeverage),
      };
      return { ...forms, gh };
    }

    case 'SmartGridHedge': {
      const skim = c.skimMode === 0 || c.skimMode === 1 || c.skimMode === 2 ? c.skimMode : forms.sgh.skimMode;
      const sgh: SghForm = {
        ...forms.sgh,
        lotUsd: str(c.lotUsd, forms.sgh.lotUsd),
        // Stored as a fraction, displayed as percent.
        stepPct: typeof c.step === 'number' ? String(Number((c.step * 100).toPrecision(10))) : forms.sgh.stepPct,
        nUp: str(c.nUp, forms.sgh.nUp),
        nDown: str(c.nDown, forms.sgh.nDown),
        skimMode: skim,
        leverage: str(c.leverage, forms.sgh.leverage),
        qHedgeOverride: c.qHedgeOverride == null ? '' : optStr(c.qHedgeOverride),
        autoRestart: bool(c.autoRestart, forms.sgh.autoRestart),
        makerFeeBps: str(c.makerFeeBps, forms.sgh.makerFeeBps),
        takerFeeBps: str(c.takerFeeBps, forms.sgh.takerFeeBps),
        takeProfitEnabled: bool(c.takeProfitEnabled, forms.sgh.takeProfitEnabled),
        takeProfitTargetUsd: str(c.takeProfitTargetUsd, forms.sgh.takeProfitTargetUsd),
      };
      return { ...forms, sgh };
    }

    case 'SpotPingPong': {
      const levels = Array.isArray(c.levels)
        ? (c.levels as Json[]).map((l) => ({
            buyPrice: str(l.buyPrice, ''),
            sellPrice: str(l.sellPrice, ''),
            amountUsdt: str(l.amountUsdt, ''),
          }))
        : forms.sppLevels;
      return { ...forms, sppLevels: levels.length ? levels : forms.sppLevels };
    }

    case 'MaratGGrid':
    case 'MaratG':
    default: {
      const mg: MgForm = {
        ...forms.mg,
        timeframe: str(c.timeframe, forms.mg.timeframe),
        indicatorType: str(c.indicatorType, forms.mg.indicatorType),
        indicatorLength: str(c.indicatorLength, forms.mg.indicatorLength),
        candleCount: str(c.candleCount, forms.mg.candleCount),
        offsetPercent: str(c.offsetPercent, forms.mg.offsetPercent),
        takeProfitPercent: str(c.takeProfitPercent, forms.mg.takeProfitPercent),
        stopLossPercent: str(c.stopLossPercent, forms.mg.stopLossPercent),
        orderSize: str(c.orderSize, forms.mg.orderSize),
        useMartingale: bool(c.useMartingale, forms.mg.useMartingale),
        martingaleCoeff: str(c.martingaleCoeff, forms.mg.martingaleCoeff),
        useSteppedMartingale: bool(c.useSteppedMartingale, forms.mg.useSteppedMartingale),
        martingaleStep: str(c.martingaleStep, forms.mg.martingaleStep),
        onlyLong: bool(c.onlyLong, forms.mg.onlyLong),
        onlyShort: bool(c.onlyShort, forms.mg.onlyShort),
        useDrawdownScale: bool(c.useDrawdownScale, forms.mg.useDrawdownScale),
        drawdownBalance: str(c.drawdownBalance, forms.mg.drawdownBalance),
        drawdownPercent: str(c.drawdownPercent, forms.mg.drawdownPercent),
        drawdownTarget: str(c.drawdownTarget, forms.mg.drawdownTarget),
        gridLotSize: str(c.gridLotSize, forms.mg.gridLotSize),
        gridStepPercent: str(c.gridStepPercent, forms.mg.gridStepPercent),
        gridMaxLots: str(c.gridMaxLots, forms.mg.gridMaxLots),
      };
      return { ...forms, mg };
    }
  }
}
