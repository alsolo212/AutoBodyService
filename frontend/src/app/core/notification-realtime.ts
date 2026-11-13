import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, effect, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { Subject, catchError, map, of, switchMap } from 'rxjs';
import { AuthService } from './auth';

export interface NotificationItem {
  id: string;
  type: string;
  title: string;
  message: string;
  createdAtUtc: string;
  readAtUtc: string | null;
  link: string | null;
}

@Injectable({ providedIn: 'root' })
export class NotificationRealtime {
  private readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly refreshRequests = new Subject<number>();
  private readonly items = signal<NotificationItem[]>([]);
  readonly history = this.items.asReadonly();
  private connection: HubConnection | null = null;
  private account: string | null = null;
  private generation = 0;

  constructor() {
    this.refreshRequests.pipe(switchMap(generation => this.account === null ? of(null) :
      this.http.get<NotificationItem[]>('/api/notifications').pipe(
        map(items => ({ items, generation })), catchError((error: HttpErrorResponse) => {
          if ((error.status === 401 || error.status === 403) && generation === this.generation) {
            this.items.set([]);
            this.auth.session.set(null);
          }
          return of(null);
        }),
      ))).subscribe(result => { if (result && result.generation === this.generation && this.account !== null) this.items.set(result.items); });
    effect(() => {
      const session = this.auth.session();
      const account = session && !session.mustChangePassword ? session.phone : null;
      if (account === this.account) return;
      this.account = account;
      const generation = ++this.generation;
      const previous = this.connection;
      this.connection = null;
      this.items.set([]);
      this.refreshRequests.next(generation);
      if (previous) void previous.stop();
      if (account) void this.connect(generation);
    });
  }

  private async connect(generation: number): Promise<void> {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/notifications', { transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 5000 })
      .configureLogging(LogLevel.None)
      .build();
    this.connection = connection;
    connection.on('NotificationsChanged', () => { if (generation === this.generation) this.refreshRequests.next(generation); });
    connection.onreconnected(() => { if (generation === this.generation) this.refreshRequests.next(generation); });
    while (generation === this.generation) {
      try {
        await connection.start();
        if (generation !== this.generation) { await connection.stop(); return; }
        this.refreshRequests.next(generation);
        return;
      } catch {
        // Retrying a failed connection is not a periodic history poll.
        await new Promise(resolve => setTimeout(resolve, 5000));
      }
    }
  }
}
