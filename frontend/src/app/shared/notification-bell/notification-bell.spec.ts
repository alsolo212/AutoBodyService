import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { AuthService } from '../../core/auth';
import { NotificationRealtime } from '../../core/notification-realtime';
import { NotificationBell, notificationLink } from './notification-bell';

describe('NotificationBell', () => {
  it('shows unread count, recent history and closes by Escape', () => {
    const session = signal({ name: 'Test', phone: '+380000000001', roles: ['Admin'], mustChangePassword: false });
    const history = signal([{ id: '1', title: 'Нова подія', message: 'Текст', createdAtUtc: '2030-01-01T00:00:00Z', readAtUtc: null, link: null, type: 'Test' }]);
    const api = { history, unreadCount: computed(() => history().filter(n => !n.readAtUtc).length), loading: signal(false), loaded: signal(true), error: signal(''), refresh: () => {} };
    TestBed.configureTestingModule({ imports: [NotificationBell], providers: [provideRouter([]), { provide: AuthService, useValue: { session } }, { provide: NotificationRealtime, useValue: api }] });
    const fixture = TestBed.createComponent(NotificationBell); fixture.componentRef.setInput('area', 'admin'); fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement; const details = root.querySelector('details')!; const summary = root.querySelector('summary')!;
    expect(summary.getAttribute('aria-label')).toContain('1'); details.open = true; fixture.detectChanges();
    expect(root.textContent).toContain('Нова подія'); expect(root.querySelector('.bell__status')?.textContent).toContain('Нове'); expect(root.querySelector('.bell__all')?.getAttribute('href')).toBe('/admin/notifications');
    details.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); fixture.detectChanges();
    expect(details.open).toBe(false); expect(document.activeElement).toBe(summary);
  });
  it('allows only local appointment links', () => {
    const id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    expect(notificationLink(`/admin/appointments/${id}`)).toBe(`/admin/appointments/${id}`);
    expect(notificationLink('//example.invalid')).toBeNull(); expect(notificationLink(`/admin/appointments/${id}?token=x`)).toBeNull();
  });
  it('places the all link above recent items and marks only an opened item, with retry after failure', () => {
    const session = signal({ name: 'Test', phone: '+380000000001', roles: ['User'], mustChangePassword: false });
    const history = signal([{ id: 'one', title: 'Подія', message: 'Текст', createdAtUtc: '2030-01-01T00:00:00Z', readAtUtc: null, link: null, type: 'Test' }]);
    const markRead = vi.fn().mockReturnValueOnce(throwError(() => new Error('offline'))).mockReturnValue(of(void 0));
    const api = { history, unreadCount: computed(() => 1), loading: signal(false), loaded: signal(true), error: signal(''), refresh: vi.fn(), markRead };
    TestBed.configureTestingModule({ imports: [NotificationBell], providers: [provideRouter([]), { provide: AuthService, useValue: { session } }, { provide: NotificationRealtime, useValue: api }] });
    const fixture = TestBed.createComponent(NotificationBell); fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.bell__heading .bell__all')?.getAttribute('href')).toBe('/account/notifications');
    expect(root.querySelector('.bell__heading')!.compareDocumentPosition(root.querySelector('.bell__panel ul')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const details = root.querySelector('.bell__item') as HTMLDetailsElement;
    details.open = true; details.dispatchEvent(new Event('toggle')); fixture.detectChanges();
    expect(markRead).toHaveBeenCalledOnce(); expect(markRead).toHaveBeenCalledWith('one');
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Не вдалося позначити');
    (root.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    expect(markRead).toHaveBeenCalledTimes(2); expect(history()[0].readAtUtc).toBeNull();
  });
});
