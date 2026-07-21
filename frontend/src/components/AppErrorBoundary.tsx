import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

export class AppErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // This is the single integration point for a future browser error-reporting service.
    console.error("Sharpbill UI error", error, info.componentStack);
  }

  private retry = () => this.setState({ error: null });

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <main className="error-boundary">
        <section className="panel panel--brackets" role="alert" aria-labelledby="ui-error-title">
          <div className="panel-header">// RECOVERY</div>
          <div className="panel-body">
            <h1 className="page-title" id="ui-error-title">The console hit an unexpected error.</h1>
            <p className="page-sub">
              Your session is unchanged. Try the view again, reload the latest application, or return home.
            </p>
            <div className="error-boundary-actions">
              <button className="btn btn-primary" type="button" onClick={this.retry}>Try again</button>
              <button className="btn btn-ghost" type="button" onClick={() => window.location.reload()}>
                Reload application
              </button>
              <a className="btn btn-ghost" href="/">Return home</a>
            </div>
          </div>
        </section>
      </main>
    );
  }
}
