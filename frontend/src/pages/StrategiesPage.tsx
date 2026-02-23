import Header from '../components/Layout/Header';

interface StrategyInfo {
  type: string;
  name: string;
  description: string;
  tags: string[];
}

const availableStrategies: StrategyInfo[] = [
  {
    type: 'MaratG',
    name: 'MaratG',
    description:
      'Бот следит за трендом по скользящей средней (EMA/SMA). Когда цена стабильно идёт выше или ниже линии на протяжении заданного количества свечей и затем касается её — открывается сделка в направлении тренда. Тейк-профит и стоп-лосс выставляются автоматически.',
    tags: ['Тренд', 'EMA / SMA', 'Фьючерсы'],
  },
];

export default function StrategiesPage() {
  return (
    <div>
      <Header title="Стратегии" subtitle="Доступные торговые стратегии" />

      <div className="grid gap-4">
        {availableStrategies.map((strategy) => (
          <div
            key={strategy.type}
            className="bg-bg-secondary rounded-xl border border-border p-6"
          >
            <div className="flex items-center gap-3 mb-3">
              <h3 className="text-lg font-semibold text-text-primary">{strategy.name}</h3>
              <div className="flex gap-1.5">
                {strategy.tags.map((tag) => (
                  <span
                    key={tag}
                    className="px-2.5 py-0.5 text-[11px] font-medium rounded-full bg-accent-blue/10 text-accent-blue"
                  >
                    {tag}
                  </span>
                ))}
              </div>
            </div>
            <p className="text-sm text-text-secondary leading-relaxed">{strategy.description}</p>
          </div>
        ))}
      </div>
    </div>
  );
}
