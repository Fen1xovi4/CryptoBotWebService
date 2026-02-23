import Header from '../components/Layout/Header';
import { useThemeStore } from '../stores/themeStore';

export default function SettingsPage() {
  const { theme, setTheme } = useThemeStore();

  return (
    <div>
      <Header title="Settings" subtitle="Configure your preferences" />

      <div className="bg-bg-secondary rounded-xl border border-border p-6 max-w-2xl">
        <h3 className="text-sm font-semibold text-text-primary mb-4">Appearance</h3>

        <div className="flex gap-4">
          <button
            onClick={() => setTheme('dark')}
            className={`flex-1 rounded-xl border-2 p-4 transition-all ${
              theme === 'dark'
                ? 'border-accent-blue shadow-lg shadow-accent-blue/20'
                : 'border-border hover:border-text-secondary'
            }`}
          >
            {/* Dark preview */}
            <div className="rounded-lg overflow-hidden mb-3">
              <div className="bg-[#1a1d2e] p-3 space-y-2">
                <div className="flex gap-2">
                  <div className="w-16 h-2 rounded bg-[#333a50]" />
                  <div className="w-10 h-2 rounded bg-[#333a50]" />
                </div>
                <div className="bg-[#242838] rounded p-2 space-y-1.5">
                  <div className="w-full h-1.5 rounded bg-[#333a50]" />
                  <div className="w-3/4 h-1.5 rounded bg-[#333a50]" />
                  <div className="w-1/2 h-1.5 rounded bg-[#333a50]" />
                </div>
              </div>
            </div>
            <p className="text-sm font-medium text-text-primary">Dark</p>
            <p className="text-xs text-text-secondary mt-0.5">Easy on the eyes</p>
          </button>

          <button
            onClick={() => setTheme('light')}
            className={`flex-1 rounded-xl border-2 p-4 transition-all ${
              theme === 'light'
                ? 'border-accent-blue shadow-lg shadow-accent-blue/20'
                : 'border-border hover:border-text-secondary'
            }`}
          >
            {/* Light preview */}
            <div className="rounded-lg overflow-hidden mb-3">
              <div className="bg-[#f5f7fa] p-3 space-y-2">
                <div className="flex gap-2">
                  <div className="w-16 h-2 rounded bg-[#d1d5db]" />
                  <div className="w-10 h-2 rounded bg-[#d1d5db]" />
                </div>
                <div className="bg-white rounded p-2 space-y-1.5">
                  <div className="w-full h-1.5 rounded bg-[#d1d5db]" />
                  <div className="w-3/4 h-1.5 rounded bg-[#d1d5db]" />
                  <div className="w-1/2 h-1.5 rounded bg-[#d1d5db]" />
                </div>
              </div>
            </div>
            <p className="text-sm font-medium text-text-primary">Light</p>
            <p className="text-xs text-text-secondary mt-0.5">Classic bright look</p>
          </button>
        </div>
      </div>
    </div>
  );
}
