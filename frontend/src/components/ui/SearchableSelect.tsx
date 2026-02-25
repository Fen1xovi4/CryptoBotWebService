import { useState, useRef, useEffect, useMemo } from 'react';

interface SearchableSelectProps {
  value: string;
  onChange: (value: string) => void;
  options: string[];
  placeholder?: string;
  isLoading?: boolean;
  disabled?: boolean;
  className?: string;
}

export default function SearchableSelect({
  value,
  onChange,
  options,
  placeholder = 'Поиск...',
  isLoading = false,
  disabled = false,
  className = '',
}: SearchableSelectProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [search, setSearch] = useState('');
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const filtered = useMemo(() => {
    if (!search) return options;
    const q = search.toUpperCase();
    return options.filter((s) => s.includes(q));
  }, [options, search]);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
        setSearch('');
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const handleOpen = () => {
    if (disabled) return;
    setIsOpen(true);
    setSearch('');
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const handleSelect = (symbol: string) => {
    onChange(symbol);
    setIsOpen(false);
    setSearch('');
  };

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={handleOpen}
        disabled={disabled}
        className={`${className} text-left flex items-center justify-between`}
      >
        <span className={value ? 'text-text-primary' : 'text-text-secondary'}>
          {isLoading ? 'Загрузка...' : value || placeholder}
        </span>
        <svg
          className="w-4 h-4 text-text-secondary shrink-0 ml-2"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          strokeWidth={2}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {isOpen && (
        <div className="absolute z-50 mt-1 w-full bg-bg-secondary border border-border rounded-lg shadow-xl overflow-hidden">
          <div className="p-2 border-b border-border">
            <input
              ref={inputRef}
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Поиск символа..."
              className="w-full bg-bg-tertiary border border-border rounded px-2 py-1.5 text-sm text-text-primary focus:outline-none focus:border-accent-blue"
            />
          </div>
          <div className="max-h-60 overflow-y-auto">
            {filtered.length === 0 ? (
              <div className="px-3 py-2 text-sm text-text-secondary">
                {isLoading ? 'Загрузка...' : 'Не найдено'}
              </div>
            ) : (
              filtered.map((symbol) => (
                <button
                  key={symbol}
                  type="button"
                  onClick={() => handleSelect(symbol)}
                  className={`w-full text-left px-3 py-1.5 text-sm hover:bg-accent-blue/10 transition-colors ${
                    symbol === value
                      ? 'text-accent-blue bg-accent-blue/5'
                      : 'text-text-primary'
                  }`}
                >
                  {symbol}
                </button>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
