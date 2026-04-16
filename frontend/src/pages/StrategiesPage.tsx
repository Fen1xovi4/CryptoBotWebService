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
  {
    type: 'HuntingFunding',
    name: 'HuntingFunding',
    description:
      'Бот охотится на движения цены вокруг списания фандинга. При высоком фандинге (например −2.5%) цена резко проседает в момент списания и затем отскакивает. Бот выставляет лимитные ордера на нескольких уровнях ниже/выше цены за 5–10 секунд до фандинга, собирает исполнения на шпильке и закрывает позицию на отскоке по тейк-профиту, стоп-лоссу или таймауту. Поддерживает циклический режим — автоматически повторяет стратегию каждые 8 часов.',
    tags: ['Фандинг', 'Лимитные ордера', 'Фьючерсы', 'Циклы'],
  },
  {
    type: 'SmaDca',
    name: 'SMA DCA',
    description:
      'Бот входит в сделку по сигналу скользящей средней (SMA): при закрытии свечи выше SMA открывает Long, ниже — Short (направление задаётся в настройках бота — одно направление на бота). Если цена уходит против позиции, срабатывает усреднение (DCA): добавляется докупка, объём которой в N раз больше текущего накопленного (обычно ×2–×3), и средняя цена входа «съезжает» ближе к рынку. После каждой докупки бот пересчитывает тейк-профит от новой средней — достаточно небольшого отскока, чтобы закрыть всю позицию в плюс. База шага DCA настраивается: от средней цены (сетка сжимается) или от цены последней докупки (равномерная сетка). Количество докупок ограничено параметром Max DCA. Стоп-лосса нет — это важно учитывать. Tейк-профит проверяется интрабарно по текущей цене каждые 5 секунд.',
    tags: ['SMA', 'Усреднение / DCA', 'Фьючерсы', 'Long или Short'],
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
