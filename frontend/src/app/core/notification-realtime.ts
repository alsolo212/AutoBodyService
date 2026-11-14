import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, InjectionToken, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HubConnection, HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { Observable, Subject, Subscription, catchError, finalize, map, of, switchMap, tap, throwError } from 'rxjs';
import { AuthService, AuthSession } from './auth';

function accountKey(session: AuthSession | null): string | null {
  return session && !session.mustChangePassword ? `${session.phone}|${[...session.roles].sort().join(',')}` : null;
}

export interface NotificationItem {
  id: string;
  type: string;
  title: string;
  message: string;
  createdAtUtc: string;
  readAtUtc: string | null;
  link: string | null;
}

export const NOTIFICATION_HUB_FACTORY = new InjectionToken<() => HubConnection>('notification hub factory', {
  providedIn: 'root',
  factory: () => () => new HubConnectionBuilder()
    .withUrl('/hubs/notifications', { transport: HttpTransportType.WebSockets, skipNegotiation: true })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 5000 })
    .configureLogging(LogLevel.None)
    .build(),
});

@Injectable({ providedIn: 'root' })
export class NotificationRealtime {
  private readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly createConnection = inject(NOTIFICATION_HUB_FACTORY);
  private readonly refreshRequests = new Subject<number>();
  private readonly items = signal<NotificationItem[]>([]);
  readonly history = this.items.asReadonly();
  readonly unreadCount = computed(() => this.items().filter(item => item.readAtUtc === null).length);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly error = signal('');
  private connection: HubConnection | null = null;
  private sessionCheck: Subscription | null = null;
  private account: string | null = null;
  private generation = 0;

  constructor() {
    this.refreshRequests.pipe(switchMap(generation => {
      if (this.account === null || generation !== this.generation || this.auth.passwordChanging()) return of(null);
      this.loading.set(true); this.error.set('');
      const passwordRevision = this.auth.passwordChangeRevision();
      return this.http.get<NotificationItem[]>('/api/notifications').pipe(
        map(items => ({ items, generation, passwordRevision })),
        catchError((error: HttpErrorResponse) => {
          if (generation === this.generation) {
            if (error.status === 401 || error.status === 403) {
              if (this.auth.passwordChanging() || passwordRevision !== this.auth.passwordChangeRevision()) return of(null);
              this.items.set([]); this.auth.session.set(null);
              this.error.set('Сесію завершено. Увійдіть знову.');
            } else this.error.set('Не вдалося завантажити сповіщення. Спробуйте ще раз.');
          }
          return of(null);
        }),
        finalize(() => { if (generation === this.generation) this.loading.set(false); }),
      );
    }), takeUntilDestroyed(this.destroyRef)).subscribe(result => {
      if (!result || result.generation !== this.generation || result.passwordRevision !== this.auth.passwordChangeRevision() || this.account === null) return;
      const unique = new Map(result.items.map(item => [item.id, item]));
      // Stable sorting preserves the server's tie-break order for equal millisecond timestamps.
      this.items.set([...unique.values()].sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc)));
      this.loaded.set(true);
    });
    effect(() => {
      const session = this.auth.session();
      const account = accountKey(session);
      if (account === this.account) return;
      this.account = account;
      const generation = ++this.generation;
      const previous = this.connection;
      this.connection = null;
      this.sessionCheck?.unsubscribe();
      this.items.set([]); this.loading.set(false); this.loaded.set(false); this.error.set('');
      this.refreshRequests.next(generation);
      if (previous) void previous.stop();
      if (account && !this.auth.passwordChanging()) void this.connect(generation);
    });
    this.auth.passwordChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(phase => {
      if (phase === 'start') {
        ++this.generation;
        this.sessionCheck?.unsubscribe();
        const previous = this.connection;
        this.connection = null;
        this.refreshRequests.next(this.generation);
        this.loading.set(false);
        if (previous) void previous.stop();
      } else if (this.account !== null) this.revalidate(this.generation, true, true);
    });
    const online = () => { if (this.account !== null) this.revalidate(this.generation, false, true); };
    window.addEventListener('online', online);
    this.destroyRef.onDestroy(() => { window.removeEventListener('online', online); ++this.generation; void this.connection?.stop(); });
  }

  refresh(): void { if (this.account !== null && !this.auth.passwordChanging()) this.refreshRequests.next(this.generation); }

  markRead(id: string): Observable<void> {
    const passwordRevision = this.auth.passwordChangeRevision();
    return this.http.get<void>('/api/auth/antiforgery').pipe(
      switchMap(() => this.http.patch<void>(`/api/notifications/${encodeURIComponent(id)}/read`, null)),
      tap(() => this.refresh()),
      catchError(error => this.handleActionError(error, passwordRevision)),
    );
  }

  markAllRead(): Observable<void> {
    const passwordRevision = this.auth.passwordChangeRevision();
    return this.http.get<void>('/api/auth/antiforgery').pipe(
      switchMap(() => this.http.post<void>('/api/notifications/read-all', null)),
      tap(() => this.refresh()),
      catchError(error => this.handleActionError(error, passwordRevision)),
    );
  }

  private handleActionError(error: unknown, passwordRevision: number): Observable<never> {
    if (error instanceof HttpErrorResponse && !this.auth.passwordChanging() && passwordRevision === this.auth.passwordChangeRevision() && (error.status === 401 || error.status === 403)) this.auth.session.set(null);
    return throwError(() => error);
  }

  private revalidate(generation: number, sync = false, resume = false): void {
    if (this.auth.passwordChanging()) return;
    const passwordRevision = this.auth.passwordChangeRevision();
    this.sessionCheck?.unsubscribe();
    this.sessionCheck = this.http.get<AuthSession>('/api/auth/session').subscribe({
      next: session => {
        if (generation !== this.generation || passwordRevision !== this.auth.passwordChangeRevision()) return;
        this.auth.session.set(session);
        if (accountKey(session) !== this.account) return;
        if (resume && (!this.connection || this.connection.state === HubConnectionState.Disconnected)) { void this.connect(generation); return; }
        if (sync) this.refresh();
      },
      error: (error: HttpErrorResponse) => {
        if (generation === this.generation && !this.auth.passwordChanging() && passwordRevision === this.auth.passwordChangeRevision() && (error.status === 401 || error.status === 403)) this.auth.session.set(null);
      },
    });
  }

  private async connect(generation: number): Promise<void> {
    const connection = this.createConnection();
    this.connection = connection;
    connection.on('NotificationsChanged', () => { if (generation === this.generation) this.refresh(); });
    connection.onreconnecting(() => { if (generation === this.generation) this.revalidate(generation); });
    connection.onreconnected(() => { if (generation === this.generation) this.revalidate(generation, true); });
    // SignalR can close cleanly without entering automatic reconnect (for example,
    // a server-side session revocation). Verify the cookie before starting again.
    connection.onclose(() => { if (generation === this.generation) this.revalidate(generation, false, true); });
    let checkedFailure = false;
    while (generation === this.generation) {
      try {
        await connection.start();
        if (generation !== this.generation) { await connection.stop(); return; }
        this.refresh();
        return;
      } catch {
        if (!checkedFailure && generation === this.generation) { checkedFailure = true; this.revalidate(generation); }
        await new Promise(resolve => setTimeout(resolve, 5000));
      }
    }
  }
}
