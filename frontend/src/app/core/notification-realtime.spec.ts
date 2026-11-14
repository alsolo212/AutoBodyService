import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { AuthService } from './auth';
import { NOTIFICATION_HUB_FACTORY, NotificationItem, NotificationRealtime } from './notification-realtime';

const mock: { connections: any[] } = { connections: [] };
function createConnection() {
  const handlers: Record<string, (...args: any[]) => void> = {};
  const connection = {
    handlers, on: (name: string, callback: (...args: any[]) => void) => handlers[name] = callback,
    onreconnecting: (callback: (...args: any[]) => void) => handlers['reconnecting'] = callback,
    onreconnected: (callback: (...args: any[]) => void) => handlers['reconnected'] = callback,
    onclose: (callback: (...args: any[]) => void) => handlers['closed'] = callback,
    state: 'Disconnected', start: vi.fn(), stop: vi.fn(),
  };
  connection.start.mockImplementation(async () => { connection.state = 'Connected'; });
  connection.stop.mockImplementation(async () => { connection.state = 'Disconnected'; });
  mock.connections.push(connection); return connection as unknown as import('@microsoft/signalr').HubConnection;
}

const user = { name: 'Клієнт', phone: '+380000000001', roles: ['User'], mustChangePassword: false };
const item = (id: string, date: string, readAtUtc: string | null = null): NotificationItem => ({ id, type: 'AppointmentCreated', title: id, message: 'Текст', createdAtUtc: date, readAtUtc, link: null });

describe('NotificationRealtime', () => {
  let service: NotificationRealtime; let auth: AuthService; let http: HttpTestingController;
  beforeEach(() => {
    mock.connections.length = 0;
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: NOTIFICATION_HUB_FACTORY, useValue: createConnection }] });
    auth = TestBed.inject(AuthService); http = TestBed.inject(HttpTestingController); service = TestBed.inject(NotificationRealtime);
  });
  afterEach(() => { http.verify(); TestBed.resetTestingModule(); });
  async function signIn() {
    auth.session.set(user); TestBed.tick();
    http.expectOne('/api/notifications').flush([]);
    await Promise.resolve();
    http.expectOne('/api/notifications').flush([]);
  }
  it('deduplicates and orders persisted history after a hub signal', async () => {
    await signIn();
    mock.connections[0].handlers['NotificationsChanged']();
    http.expectOne('/api/notifications').flush([item('older', '2030-01-01T00:00:00Z'), item('newer', '2030-01-02T00:00:00Z'), item('older', '2030-01-01T00:00:00Z')]);
    expect(service.history().map(x => x.id)).toEqual(['newer', 'older']); expect(service.unreadCount()).toBe(2);
    mock.connections[0].handlers['NotificationsChanged']();
    http.expectOne('/api/notifications').flush([item('older', '2030-01-01T00:00:00Z'), item('newer', '2030-01-02T00:00:00Z')]);
    expect(service.history()).toHaveLength(2);
  });
  it('revalidates on reconnect and performs a single REST catch-up', async () => {
    await signIn(); const hub = mock.connections[0];
    hub.handlers['reconnecting'](); http.expectOne('/api/auth/session').flush(user);
    hub.handlers['reconnected'](); http.expectOne('/api/auth/session').flush(user);
    http.expectOne('/api/notifications').flush([item('missed', '2030-01-01T00:00:00Z')]);
    expect(service.history().map(x => x.id)).toEqual(['missed']);
    http.expectNone('/api/notifications');
  });
  it('clears and disconnects on logout, forbidden session and role changes', async () => {
    await signIn(); const first = mock.connections[0];
    auth.session.set({ ...user, roles: ['Admin'] }); TestBed.tick();
    expect(first.stop).toHaveBeenCalledOnce();
    http.expectOne('/api/notifications').flush([]); await Promise.resolve(); http.expectOne('/api/notifications').flush([]);
    const second = mock.connections[1]; second.handlers['reconnecting']();
    http.expectOne('/api/auth/session').flush(null, { status: 401, statusText: 'Unauthorized' }); TestBed.tick();
    expect(second.stop).toHaveBeenCalledOnce(); expect(auth.session()).toBeNull(); expect(service.history()).toEqual([]);
    auth.session.set(user); TestBed.tick(); http.expectOne('/api/notifications').flush([]); await Promise.resolve(); http.expectOne('/api/notifications').flush([]);
    const third = mock.connections[2]; auth.session.set(null); TestBed.tick(); expect(third.stop).toHaveBeenCalledOnce();
  });
  it('checks a clean server close and stops when the cookie has been revoked', async () => {
    await signIn(); const hub = mock.connections[0]; hub.state = 'Disconnected'; hub.handlers['closed']();
    http.expectOne('/api/auth/session').flush(null, { status: 401, statusText: 'Unauthorized' }); TestBed.tick();
    expect(auth.session()).toBeNull(); expect(hub.stop).toHaveBeenCalledOnce(); expect(service.history()).toEqual([]);
  });
  it('starts a fresh connection after a clean close when the cookie remains valid', async () => {
    await signIn(); const hub = mock.connections[0]; hub.state = 'Disconnected'; hub.handlers['closed']();
    http.expectOne('/api/auth/session').flush(user); expect(mock.connections).toHaveLength(2);
    await Promise.resolve(); http.expectOne('/api/notifications').flush([item('restored', '2030-01-01T00:00:00Z')]);
    expect(service.history()[0].id).toBe('restored');
  });
  it('preserves the session during a network failure and accepts a later valid cookie', async () => {
    await signIn(); const hub = mock.connections[0];
    hub.handlers['reconnecting'](); http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    expect(auth.session()).toEqual(user);
    hub.handlers['reconnected'](); http.expectOne('/api/auth/session').flush(user);
    http.expectOne('/api/notifications').flush([item('restored', '2030-01-01T00:00:00Z')]);
    expect(service.history()[0].id).toBe('restored');
  });
  it('keeps a password-changing session through stamp rotation and revalidates the refreshed cookie', async () => {
    await signIn(); const hub = mock.connections[0];
    auth.changePassword('old-password', 'new-password').subscribe();
    expect(hub.stop).toHaveBeenCalledOnce();
    http.expectOne('/api/auth/antiforgery').flush(null); const changing = http.expectOne('/api/auth/change-password');
    hub.handlers['reconnecting'](); http.expectNone('/api/auth/session');
    changing.flush(null);
    expect(auth.session()).toEqual(user);
    http.expectOne('/api/auth/session').flush(user); await Promise.resolve();
    http.expectOne('/api/notifications').flush([item('secure', '2030-01-01T00:00:00Z')]);
    expect(service.history()[0].id).toBe('secure');
  });
  it('does not restore a logged-out account from a late session response', async () => {
    await signIn(); const hub = mock.connections[0];
    hub.handlers['reconnecting'](); const old = http.expectOne('/api/auth/session');
    auth.session.set(null); TestBed.tick(); expect(old.cancelled).toBe(true);
    expect(auth.session()).toBeNull(); expect(service.history()).toEqual([]);
  });
  it('uses antiforgery for read operations and refreshes only after success', async () => {
    await signIn(); service.markRead('id/unsafe').subscribe();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const read = http.expectOne('/api/notifications/id%2Funsafe/read'); expect(read.request.method).toBe('PATCH'); read.flush(null);
    http.expectOne('/api/notifications').flush([item('one', '2030-01-01T00:00:00Z', '2030-01-01T01:00:00Z')]);
    expect(service.unreadCount()).toBe(0);
    service.markAllRead().subscribe(); http.expectOne('/api/auth/antiforgery').flush(null);
    const all = http.expectOne('/api/notifications/read-all'); expect(all.request.method).toBe('POST'); all.flush(null);
    http.expectOne('/api/notifications').flush([]); expect(service.history()).toEqual([]);
  });
});
