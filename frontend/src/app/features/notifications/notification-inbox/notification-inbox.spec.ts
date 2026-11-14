import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { NotificationRealtime } from '../../../core/notification-realtime';
import { AuthService } from '../../../core/auth';
import { NotificationInbox } from './notification-inbox';

const appointmentId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const make = (id: string, readAtUtc: string | null, link: string | null = null) => ({ id, title: `Подія ${id}`, message: 'Текст сповіщення', type: 'Test', createdAtUtc: '2031-10-26T01:30:00Z', readAtUtc, link });

describe('NotificationInbox', () => {
  const history = signal([make('new', null, `/account/appointments/${appointmentId}`), make('read', '2031-10-26T02:00:00Z', 'https://example.invalid')]);
  const api = { history, unreadCount: computed(() => history().filter(n => !n.readAtUtc).length), loading: signal(false), loaded: signal(true), error: signal(''), refresh: vi.fn(), markRead: vi.fn(() => of(void 0)), markAllRead: vi.fn(() => of(void 0)) };
  beforeEach(() => {
    history.set([make('new', null, `/account/appointments/${appointmentId}`), make('read', '2031-10-26T02:00:00Z', 'https://example.invalid')]);
    api.error.set(''); api.loading.set(false); api.loaded.set(true); vi.clearAllMocks();
    TestBed.configureTestingModule({ imports: [NotificationInbox], providers: [provideRouter([]), { provide: NotificationRealtime, useValue: api }, { provide: AuthService, useValue: { session: signal({ name: 'Test', phone: '000', roles: ['User'], mustChangePassword: false }) } }] });
  });
  it('shows latest history, Kyiv repeated-hour offset, safe links and unread filter', () => {
    const fixture = TestBed.createComponent(NotificationInbox); fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(api.refresh).toHaveBeenCalledOnce(); expect(root.querySelectorAll('.inbox__item')).toHaveLength(2);
    expect(root.textContent).toContain('03:30'); expect(root.textContent).toContain('UTC+02:00');
    expect(root.querySelectorAll('.inbox__item a')).toHaveLength(1);
    expect(root.querySelector('.inbox__item a')?.getAttribute('href')).toBe(`/account/appointments/${appointmentId}`);
    fixture.componentInstance.filter.set('unread'); fixture.detectChanges();
    expect(root.querySelectorAll('.inbox__item')).toHaveLength(1); expect(root.textContent).not.toContain('Подія read');
    history.set([make('new', '2031-10-26T02:00:00Z')]); fixture.detectChanges();
    expect(root.textContent).toContain('Непрочитаних сповіщень немає');
  });
  it('marks one or all through the shared service and reports errors', () => {
    const fixture = TestBed.createComponent(NotificationInbox); fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('.inbox__actions button') as HTMLButtonElement).click(); expect(api.markRead).toHaveBeenCalledWith('new');
    (root.querySelector('.inbox__heading button') as HTMLButtonElement).click(); expect(api.markAllRead).toHaveBeenCalledOnce();
    api.markRead.mockReturnValueOnce(throwError(() => new Error('failed')));
    fixture.componentInstance.read('new'); fixture.detectChanges(); expect(root.querySelector('[role="alert"]')?.textContent).toContain('Не вдалося оновити');
  });
  it('shows loading, empty and retriable failure states', () => {
    api.loading.set(true); api.loaded.set(false);
    const fixture = TestBed.createComponent(NotificationInbox); fixture.detectChanges(); const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Завантажуємо сповіщення');
    api.loading.set(false); api.error.set('Не вдалося завантажити'); fixture.detectChanges();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Не вдалося завантажити');
    (root.querySelector('[role="alert"] button') as HTMLButtonElement).click(); expect(api.refresh).toHaveBeenCalledTimes(2);
    api.error.set(''); history.set([]); fixture.detectChanges(); expect(root.textContent).toContain('Сповіщень поки немає');
  });
});
