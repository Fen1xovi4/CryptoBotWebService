import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
  /** Label shown above the error — helps locate where in the app the crash happened. */
  label?: string;
}

interface State {
  error: Error | null;
  info: ErrorInfo | null;
}

/**
 * Catches render-time crashes in its subtree and shows a fallback with the stack so we don't
 * end up staring at a blank page. Without this a single null-ref in any card collapses the
 * whole route. Add at route-level (or around volatile widgets) — not globally, so other parts
 * of the app keep working when one section breaks.
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, info: null };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    this.setState({ info });
    // eslint-disable-next-line no-console
    console.error('[ErrorBoundary]', this.props.label || 'route', error, info);
  }

  reset = () => this.setState({ error: null, info: null });

  render() {
    if (!this.state.error) return this.props.children;

    return (
      <div className="min-h-screen bg-bg-primary p-6">
        <div className="max-w-4xl mx-auto bg-bg-secondary border border-accent-red/40 rounded-xl p-6">
          <div className="flex items-start justify-between gap-4 mb-4">
            <div>
              <h2 className="text-lg font-semibold text-accent-red">
                Что-то сломалось при рендере {this.props.label ? `(${this.props.label})` : ''}
              </h2>
              <p className="text-sm text-text-secondary mt-1">
                Это сообщение поймал ErrorBoundary вместо того, чтобы оставить пустой экран. Скопируйте
                stack-trace ниже и пришлите — без него фиксить сложно.
              </p>
            </div>
            <button
              onClick={this.reset}
              className="px-3 py-1.5 text-xs font-medium bg-accent-blue/15 text-accent-blue rounded-lg hover:bg-accent-blue/25 transition-colors whitespace-nowrap"
            >
              Попробовать ещё
            </button>
          </div>

          <div className="font-mono text-xs bg-bg-tertiary border border-border rounded-lg p-3 overflow-x-auto">
            <div className="text-accent-red font-semibold mb-1">{this.state.error.name}: {this.state.error.message}</div>
            {this.state.error.stack && (
              <pre className="text-text-secondary whitespace-pre-wrap break-words">{this.state.error.stack}</pre>
            )}
            {this.state.info?.componentStack && (
              <>
                <div className="text-text-secondary mt-3 mb-1 font-semibold">Component stack:</div>
                <pre className="text-text-secondary whitespace-pre-wrap break-words">{this.state.info.componentStack}</pre>
              </>
            )}
          </div>
        </div>
      </div>
    );
  }
}
