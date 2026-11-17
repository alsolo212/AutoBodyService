import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth';
import { AuditLog, auditDetails } from './audit-log';

const session = { name: 'Test', phone: '+380000000099', roles: ['SuperAdmin'], mustChangePassword: false };
const entry = { id: '1', occurredAtUtc: '2026-10-25T01:30:00Z', actorId: 'actor-id', actorName: 'Олена', action: 'ExpenseCorrected', objectType: 'MonthlyExpense', objectId: 'expense-id', details: { before: { amountExact: '9007199254740993.125', hasDescription: true }, after: { amountExact: '12.34', hasDescription: false }, descriptionChanged: true } };

describe('AuditLog', () => {
  let http: HttpTestingController; let auth: AuthService;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AuditLog], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService); auth.session.set(session);
  });
  afterEach(() => http.verify());
  async function create() {
    const fixture = TestBed.createComponent(AuditLog);
    const request = http.expectOne(req => req.url === '/api/admin/audit-log');
    request.flush({ total: 11, page: 1, pageSize: 10, items: [entry] });
    await fixture.whenStable(); return fixture;
  }
  it('renders readable facts, exact safe details and no mutation controls', async () => {
    const fixture = await create(); const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Олена'); expect(text).toContain('actor-id'); expect(text).toContain('expense-id'); expect(text).toContain('UTC+02:00');
    expect(text).toContain('Виправлення витрати'); expect(text).toContain('9007199254740993,125 ₴');
    expect(fixture.nativeElement.querySelector('button[aria-label*="Видалити"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('button[aria-label*="Редагувати"]')).toBeNull();
    expect(auditDetails(entry)).toContain('Стало: 12,34 ₴; опис: ні');
  });
  it('sends Kyiv dates as exclusive UTC bounds across the autumn clock change', async () => {
    const fixture = await create(); const component = fixture.componentInstance;
    component.form.setValue({ fromDate: '2026-10-25', toDate: '2026-10-25', action: 'ExpenseCorrected', actorId: ' actor-id ', direction: 'asc' });
    component.apply();
    const req = http.expectOne(r => r.url === '/api/admin/audit-log');
    expect(req.request.params.get('fromUtc')).toBe('2026-10-24T21:00:00.000Z');
    expect(req.request.params.get('toUtc')).toBe('2026-10-25T22:00:00.000Z');
    expect(req.request.params.get('action')).toBe('ExpenseCorrected'); expect(req.request.params.get('actorId')).toBe('actor-id');
    expect(req.request.params.get('direction')).toBe('asc');
    req.flush({ total: 11, page: 1, pageSize: 10, items: [entry] });
    component.changePage(1); const next = http.expectOne(r => r.url === '/api/admin/audit-log'); expect(next.request.params.get('page')).toBe('2');
    next.flush({ total: 11, page: 2, pageSize: 10, items: [] }); await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('За цими умовами подій немає');
  });
  it('rejects reversed dates without sending a request', async () => {
    const fixture = await create(); fixture.componentInstance.form.patchValue({ fromDate: '2026-11-02', toDate: '2026-11-01' });
    fixture.componentInstance.apply(); await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Перевірте дати'); http.expectNone(r => r.url === '/api/admin/audit-log');
  });
  it('uses Kyiv spring-transition day boundaries and lets the user filter an actor', async () => {
    const fixture = await create(); const component = fixture.componentInstance;
    component.form.patchValue({ fromDate: '2026-03-29', toDate: '2026-03-29' }); component.filterActor('actor-id');
    const request = http.expectOne(r => r.url === '/api/admin/audit-log');
    expect(request.request.params.get('fromUtc')).toBe('2026-03-28T22:00:00.000Z');
    expect(request.request.params.get('toUtc')).toBe('2026-03-29T21:00:00.000Z');
    expect(request.request.params.get('actorId')).toBe('actor-id');
    request.flush({ total: 0, page: 1, pageSize: 10, items: [] });
  });
  it('clears data on session revocation and ignores the pending response', async () => {
    const fixture = await create(); fixture.componentInstance.load(); const pending = http.expectOne(r => r.url === '/api/admin/audit-log');
    auth.invalidateSession(); await fixture.whenStable(); expect(pending.cancelled).toBe(true); expect(fixture.componentInstance.result()).toBeNull();
  });
  it('rechecks session after 403 and invalidates it on 401', async () => {
    const fixture = await create(); fixture.componentInstance.load();
    http.expectOne(r => r.url === '/api/admin/audit-log').flush(null, { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/auth/session').flush(session); await fixture.whenStable(); expect(fixture.componentInstance.error()).toContain('заборонено');
    fixture.componentInstance.load(); http.expectOne(r => r.url === '/api/admin/audit-log').flush(null, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable(); expect(auth.session()).toBeNull(); expect(fixture.componentInstance.result()).toBeNull();
  });
});
