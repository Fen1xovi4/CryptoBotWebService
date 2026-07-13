import type { AllForms } from './formDefaults';
import type { StrategyType } from './types';

export interface BuildResult {
  ok: boolean;
  error?: string;
  configJson?: string;
  secondSymbol?: string;
}

const clean = (s: string) => s.replace(/\s+/g, '').toUpperCase();

/** Builds the ConfigJson string (+ optional request-level secondSymbol) for a
 * given strategy type from the tester form state. Field shapes mirror the
 * real bot-creation form 1:1 — see frontend/src/pages/ActiveBotsPage.tsx
 * `AddStrategyModal.handleSubmit` (~L4622-L4841). */
export function buildSimConfig(strategyType: StrategyType, symbol: string, forms: AllForms): BuildResult {
  const sym = clean(symbol);

  switch (strategyType) {
    case 'HuntingFunding': {
      if (forms.hfLevels.length === 0) {
        return { ok: false, error: 'Добавьте хотя бы один уровень ордеров' };
      }
      const configJson = JSON.stringify({
        symbol: sym,
        levels: forms.hfLevels,
        takeProfitPercent: Number(forms.hf.takeProfitPercent),
        stopLossPercent: Number(forms.hf.stopLossPercent),
        secondsBeforeFunding: Number(forms.hf.secondsBeforeFunding),
        closeAfterMinutes: Number(forms.hf.closeAfterMinutes),
        maxCycles: Number(forms.hf.maxCycles),
        enableLong: forms.hf.enableLong,
        minFundingLong: Number(forms.hf.minFundingLong),
        enableShort: forms.hf.enableShort,
        minFundingShort: Number(forms.hf.minFundingShort),
        // autoRotateTicker is meaningless in a fixed-window simulation — always off.
        autoRotateTicker: false,
        // Workspace-level fields, flattened for the simulator.
        fundingRateMin: Number(forms.hf.fundingRateMin),
        fundingRateMax: Number(forms.hf.fundingRateMax),
      });
      return { ok: true, configJson };
    }

    case 'SmaDca': {
      const tpShifts = forms.sdTpShifts
        .map((s) => ({ fromTier: Number(s.fromTier), takeProfitPercent: Number(s.takeProfitPercent) }))
        .filter((s) => s.fromTier > 0 && s.takeProfitPercent > 0)
        .sort((a, b) => a.fromTier - b.fromTier);
      if (forms.sd.takeProfitTierShiftEnabled && tpShifts.length === 0) {
        return { ok: false, error: 'Добавьте хотя бы одну ступень смещения TP (или выключите чекбокс)' };
      }
      const configJson = JSON.stringify({
        symbol: sym,
        timeframe: forms.sd.timeframe,
        direction: forms.sd.direction,
        smaPeriod: Number(forms.sd.smaPeriod),
        takeProfitPercent: Number(forms.sd.takeProfitPercent),
        positionSizeUsd: Number(forms.sd.positionSizeUsd),
        dcaTriggerBase: forms.sd.dcaTriggerBase,
        orderType: forms.sd.orderType,
        entryLimitOffsetPercent: Number(forms.sd.entryLimitOffsetPercent),
        entryLimitTimeoutBars: Number(forms.sd.entryLimitTimeoutBars),
        levels: forms.sdLevels.map((l) => ({
          stepPercent: Number(l.stepPercent),
          multiplier: Number(l.multiplier),
          count: Number(l.count),
        })),
        takeProfitTierShiftEnabled: forms.sd.takeProfitTierShiftEnabled,
        takeProfitTierShifts: tpShifts,
      });
      return { ok: true, configJson };
    }

    case 'FundingClaim': {
      const configJson = JSON.stringify({
        symbol: sym,
        maxCycles: Number(forms.fc.maxCycles),
        // autoRotateTicker is meaningless in a fixed-window simulation — always off.
        autoRotateTicker: false,
        checkBeforeFundingMinutes: Number(forms.fc.checkBeforeFundingMinutes),
        // Workspace-level fields, flattened for the simulator.
        fcSizeUsdt: Number(forms.fc.fcSizeUsdt),
        fcMinFundingRatePercent: Number(forms.fc.fcMinFundingRatePercent),
        fcMaxFundingRatePercent: Number(forms.fc.fcMaxFundingRatePercent),
        fcStopLossPercent: Number(forms.fc.fcStopLossPercent),
        fcLeverage: Number(forms.fc.fcLeverage),
        fcSlGraceMinutes: Number(forms.fc.fcSlGraceMinutes),
        fcSlCooldownHours: Number(forms.fc.fcSlCooldownHours),
      });
      return { ok: true, configJson };
    }

    case 'GridFloat': {
      const tiers = forms.gfTiers
        .map((t) => {
          const upToPercent = Number(t.upTo);
          const sizeUsdt = Number(t.size);
          const dcaStepPercent = t.dca.trim() === '' ? null : Number(t.dca);
          const tpStepPercent = t.tp.trim() === '' ? null : Number(t.tp);
          return { upToPercent, sizeUsdt, dcaStepPercent, tpStepPercent };
        })
        .filter((t) => t.upToPercent > 0 && t.sizeUsdt > 0)
        .sort((a, b) => a.upToPercent - b.upToPercent);
      if (tiers.length === 0) {
        return { ok: false, error: 'Добавьте хотя бы один ярус с upTo% > 0 и ставкой > 0' };
      }
      for (let i = 1; i < tiers.length; i++) {
        if (tiers[i].upToPercent <= tiers[i - 1].upToPercent) {
          return { ok: false, error: 'Ярусы должны идти со строго возрастающим upTo%' };
        }
      }
      for (const t of tiers) {
        if (t.dcaStepPercent !== null && !(t.dcaStepPercent > 0)) {
          return { ok: false, error: 'Per-tier шаг DCA должен быть > 0 (или оставьте пустым)' };
        }
        if (t.tpStepPercent !== null && !(t.tpStepPercent > 0)) {
          return { ok: false, error: 'Per-tier шаг TP должен быть > 0 (или оставьте пустым)' };
        }
      }
      const tiersPayload = tiers.map((t) => ({
        upToPercent: t.upToPercent,
        sizeUsdt: t.sizeUsdt,
        ...(t.dcaStepPercent !== null ? { dcaStepPercent: t.dcaStepPercent } : {}),
        ...(t.tpStepPercent !== null ? { tpStepPercent: t.tpStepPercent } : {}),
      }));
      const gfTpTarget = Number(forms.gf.takeProfitTargetUsd);
      if (forms.gf.takeProfitEnabled && !(gfTpTarget > 0)) {
        return { ok: false, error: 'Цель Take-Profit должна быть > 0 USDT' };
      }
      const configJson = JSON.stringify({
        symbol: sym,
        timeframe: forms.gf.timeframe,
        direction: forms.gf.direction,
        tiers: tiersPayload,
        dcaStepPercent: Number(forms.gf.dcaStepPercent),
        tpStepPercent: Number(forms.gf.tpStepPercent),
        leverage: Number(forms.gf.leverage),
        useStaticRange: forms.gf.useStaticRange,
        takeProfitEnabled: forms.gf.takeProfitEnabled,
        takeProfitTargetUsd: gfTpTarget,
      });
      return { ok: true, configJson };
    }

    case 'GridHedge': {
      const gh = forms.gh;
      if (Number(gh.rangePercent) <= 0 || Number(gh.upperExitPercent) <= 0) {
        return { ok: false, error: 'Диапазоны вниз/вверх должны быть > 0' };
      }
      if (Number(gh.dcaStepPercent) <= 0 || Number(gh.tpStepPercent) <= 0) {
        return { ok: false, error: 'Шаги DCA / TP должны быть > 0' };
      }
      if (Number(gh.betUsdt) <= 0) {
        return { ok: false, error: 'Ставка на уровень должна быть > 0' };
      }
      if (Number(gh.hedgeNotionalUsdt) < 0) {
        return { ok: false, error: 'Размер хеджа не может быть отрицательным' };
      }
      if (gh.mode === 2 && !gh.secondSymbol.trim()) {
        return { ok: false, error: 'Cross-Ticker режим требует второй тикер (например BTCUSDT)' };
      }
      const hedgeSymbol = gh.mode === 2 ? clean(gh.secondSymbol) : '';
      const configJson = JSON.stringify({
        mode: gh.mode,
        positionMode: gh.positionMode,
        gridSymbol: sym,
        hedgeSymbol,
        rangePercent: Number(gh.rangePercent),
        upperExitPercent: Number(gh.upperExitPercent),
        dcaStepPercent: Number(gh.dcaStepPercent),
        tpStepPercent: Number(gh.tpStepPercent),
        betUsdt: Number(gh.betUsdt),
        hedgeNotionalUsdt: Number(gh.hedgeNotionalUsdt),
        hedgeLeverage: Number(gh.hedgeLeverage),
        gridLeverage: Number(gh.gridLeverage),
      });
      return { ok: true, configJson, secondSymbol: gh.mode === 2 ? hedgeSymbol : undefined };
    }

    case 'SmartGridHedge': {
      const sgh = forms.sgh;
      const stepFraction = Number(sgh.stepPct) / 100;
      if (!(stepFraction > 0) || !(stepFraction < 1)) {
        return { ok: false, error: 'Шаг % должен быть от 0.05 до 99 (хранится как дробь)' };
      }
      if (Number(sgh.lotUsd) <= 0) {
        return { ok: false, error: 'Лот USDT должен быть > 0' };
      }
      if (Number(sgh.nUp) < 1 || Number(sgh.nDown) < 1) {
        return { ok: false, error: 'NUp и NDown должны быть ≥ 1' };
      }
      if (Number(sgh.leverage) < 1) {
        return { ok: false, error: 'Плечо должно быть ≥ 1' };
      }
      const qHedgeOverride = sgh.qHedgeOverride.trim() === '' ? null : Number(sgh.qHedgeOverride);
      if (qHedgeOverride !== null && !(qHedgeOverride >= 0)) {
        return { ok: false, error: 'Q_hedge override должен быть ≥ 0 или оставьте пустым (авто)' };
      }
      const tpTarget = Number(sgh.takeProfitTargetUsd);
      if (sgh.takeProfitEnabled && !(tpTarget > 0)) {
        return { ok: false, error: 'Цель Take-Profit должна быть > 0 USDT' };
      }
      const configJson = JSON.stringify({
        symbol: sym,
        lotUsd: Number(sgh.lotUsd),
        step: stepFraction,
        nUp: Number(sgh.nUp),
        nDown: Number(sgh.nDown),
        skimMode: sgh.skimMode,
        leverage: Number(sgh.leverage),
        qHedgeOverride,
        autoRestart: sgh.autoRestart,
        makerFeeBps: Number(sgh.makerFeeBps),
        takerFeeBps: Number(sgh.takerFeeBps),
        takeProfitEnabled: sgh.takeProfitEnabled,
        takeProfitTargetUsd: tpTarget,
      });
      return { ok: true, configJson };
    }

    case 'MaratG':
    default: {
      const configJson = JSON.stringify({
        indicatorType: forms.mg.indicatorType,
        indicatorLength: Number(forms.mg.indicatorLength),
        candleCount: Number(forms.mg.candleCount),
        offsetPercent: Number(forms.mg.offsetPercent),
        takeProfitPercent: Number(forms.mg.takeProfitPercent),
        stopLossPercent: Number(forms.mg.stopLossPercent),
        symbol: sym,
        timeframe: forms.mg.timeframe,
      });
      return { ok: true, configJson };
    }
  }
}

/** Pure recommendation helper for the GridHedge hedge-size hint, copied from
 * ActiveBotsPage.tsx `gridHedgeRecommendation` (~L3342). */
export function gridHedgeRecommendation(
  R: number,
  U: number,
  step: number,
  tp: number,
  G: number,
): { hedge: number; equalLoss: number; lUp: number; lDown: number } | null {
  if (!(R > 0) || !(U > 0) || !(step > 0) || !(tp > 0) || !(G > 0)) return null;

  const n = Math.floor(Math.log(1 + U / 100) / Math.log(1 + tp / 100));
  const lastAnchor = Math.pow(1 + tp / 100, n);
  const lUp = n * G * (tp / 100) + G * ((1 + U / 100) / lastAnchor - 1);

  const m = Math.round(R / step);
  let lDown = 0;
  for (let k = 0; k <= m; k++) {
    lDown += G * (1 - (1 - R / 100) / (1 - (k * step) / 100));
  }

  const hedge = ((lUp + lDown) * 100) / (R + U);
  const equalLoss = lUp - (hedge * U) / 100;
  return { hedge, equalLoss, lUp, lDown };
}
