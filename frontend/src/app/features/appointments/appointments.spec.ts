import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AppointmentList } from './appointment-list/appointment-list';
import { AppointmentDetail } from './appointment-detail/appointment-detail';

const summary = { id: 'one', make: 'Марка', model: 'Модель', maskedVin: '*************4352', inspectionStartUtc: '2026-07-01T08:00:00Z', status: 'AwaitingInspection', repairPlan: null };
const plan = { agreedPrice: 1250.5, currency: 'UAH', plannedStartUtc: '2026-07-01T08:00:00Z', estimatedFinishUtc: '2026-12-01T08:00:00Z' };
const detail = { ...summary, vin: '1HGCM82633A004352', problemDescription: 'Опис проблеми', history: [{ fromStatus: null, toStatus: 'AwaitingInspection', changedAtUtc: '2026-12-01T08:00:00Z', actorName: 'СЕКРЕТ', reason: 'ВНУТРІШНЄ' }], repairPlan: plan, participants: [{ name: 'Майстер', specialization: 'Маляр', assignedAtUtc: 'ВНУТРІШНЄ' }], customerPhone: 'СЕКРЕТ' };

describe('personal appointments pages', () => {
  let http: HttpTestingController;
  const params = new BehaviorSubject(convertToParamMap({ id: 'one' }));
  beforeEach(() => { params.next(convertToParamMap({ id: 'one' })); TestBed.configureTestingModule({ imports: [AppointmentList, AppointmentDetail], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: ActivatedRoute, useValue: { paramMap: params } }] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  it('loads own records and changes categories with empty and retry states', () => {
    const fixture = TestBed.createComponent(AppointmentList); fixture.detectChanges();
    http.expectOne('/api/health').flush({ status: 'Healthy' });
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо записи');
    http.expectOne('/api/appointments/my').flush([summary]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Марка Модель');
    expect(fixture.nativeElement.textContent).toContain('*************4352');
    expect(fixture.nativeElement.textContent).toContain('Очікує огляду');
    expect(fixture.nativeElement.textContent).toContain('11:00');
    const c = fixture.componentInstance;
    c.select('active'); const cancelled = http.expectOne('/api/appointments/my?category=active');
    c.select('completed'); expect(cancelled.cancelled).toBe(true);
    http.expectOne('/api/appointments/my?category=completed').flush([]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Записів не знайдено');
    c.select('cancelled'); http.expectOne('/api/appointments/my?category=cancelled').flush(null, { status: 500, statusText: 'Error' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити записи');
    c.select('cancelled'); http.expectOne('/api/appointments/my?category=cancelled').flush([{ ...summary, status: 'Cancelled', repairPlan: plan }]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Скасовано'); expect(fixture.nativeElement.textContent).toContain('₴'); expect(fixture.nativeElement.textContent).toContain('Планові терміни');
  });
  it('renders details, history, participants and Kyiv dates without administrative fields', () => {
    const fixture = TestBed.createComponent(AppointmentDetail); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо запис');
    http.expectOne('/api/appointments/one').flush(detail); fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    for (const value of ['1HGCM82633A004352', 'Опис проблеми', 'Очікує огляду', 'Майстер', 'Маляр', '11:00', '10:00', '₴']) expect(text).toContain(value);
    expect(text).not.toContain('СЕКРЕТ'); expect(text).not.toContain('ВНУТРІШНЄ');
  });
  it('distinguishes missing, session and retriable errors and handles absent optional data', () => {
    const fixture = TestBed.createComponent(AppointmentDetail); fixture.detectChanges();
    http.expectOne('/api/appointments/one').flush(null, { status: 404, statusText: 'Not Found' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Запис не знайдено');
    fixture.componentInstance.reload(); http.expectOne('/api/appointments/one').flush(null, { status: 401, statusText: 'Unauthorized' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Сесію завершено');
    fixture.componentInstance.reload(); http.expectOne('/api/appointments/one').flush(null, { status: 500, statusText: 'Error' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити запис');
    fixture.componentInstance.reload(); http.expectOne('/api/appointments/one').flush({ ...detail, history: [], repairPlan: null, participants: null }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Дані про вартість і терміни поки недоступні');
    expect(fixture.nativeElement.textContent).toContain('Історія статусів поки недоступна');
    expect(fixture.nativeElement.textContent).toContain('Дані про учасників поки недоступні');
  });
});
