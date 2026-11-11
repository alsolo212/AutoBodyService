import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InspectionCalendar } from './inspection-calendar';

describe('InspectionCalendar', () => {
  let http: HttpTestingController;
  const version = 'a7aa8564-66d2-4758-bdcb-c1177381d199';
  const appointment = { id: 'order-1', make: 'Test', model: 'Car', customerName: 'Client', status: 'AwaitingInspection', inspectionStartUtc: '2026-11-09T07:00:00Z' };
  beforeEach(() => { TestBed.configureTestingModule({ imports: [InspectionCalendar], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function load() {
    http.expectOne('/api/admin/inspection-schedule').flush({ version, intervals: [{ dayOfWeek: 'Monday', start: '09:00:00', end: '10:00:00' }] });
    const exception = http.expectOne(r => r.url === '/api/admin/inspection-exceptions'); const date = exception.request.params.get('date');
    exception.flush({ version, date, exception: null });
    http.expectOne(r => r.url === '/api/inspection-slots' && r.params.get('date') === date).flush({ date, slots: [{ startUtc: '2026-11-09T07:30:00Z', endUtc: '2026-11-09T08:00:00Z' }] });
    http.expectOne(r => r.url === '/api/admin/inspection-calendar' && r.params.get('date') === date).flush([appointment]);
  }
  it('starts new weekly, date and slot entries at 08:00', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load();
    const component = fixture.componentInstance;
    component.addWeekly('Tuesday'); component.addInterval(); component.addSlot();
    expect(component.weeklyIntervals.at(1).getRawValue()).toEqual({ dayOfWeek: 'Tuesday', start: '08:00', end: '08:30' });
    expect(component.dateIntervals.at(0).getRawValue()).toEqual({ start: '08:00', end: '08:30' });
    expect(component.slotRules.at(0).getRawValue()).toEqual({ start: '08:00', mode: 'Closed' });
  });
  it('shows real bookings with customer and order links alongside free slots', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load(); fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Client'); expect(text).toContain('Test Car'); expect(text).toContain('Зайнято'); expect(text).toContain('Вільно');
    expect(fixture.nativeElement.querySelector('a[href="/admin/appointments/order-1"]')).not.toBeNull();
  });
  it('previews affected orders before confirmation and rejects changed data on save', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load(); fixture.detectChanges();
    const component = fixture.componentInstance; component.setTab('settings'); component.exceptionForm.controls.mode.setValue('Closed');
    (fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).showModal = () => {};
    component.requestSave('date'); http.expectOne('/api/auth/antiforgery').flush(null);
    const preview = http.expectOne('/api/admin/inspection-exceptions/preview'); expect(preview.request.body.exception.mode).toBe('Closed');
    preview.flush({ confirmationToken: 'snapshot', conflicts: [appointment] }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Зміна вплине на такі записи'); expect(fixture.nativeElement.textContent).toContain('Client');
    component.save(); http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/inspection-exceptions'); expect(request.request.body.confirmationToken).toBe('snapshot');
    request.flush({}, { status: 409, statusText: 'Conflict' });
    expect(component.error()).toContain('записи змінилися'); expect(component.exceptionForm.controls.mode.value).toBe('Closed');
  });
  it('does not save to a date that has not been loaded', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load(); fixture.detectChanges();
    const component = fixture.componentInstance; component.dateForm.controls.date.setValue('2026-11-30'); component.requestSave('weekly');
    expect(component.error()).toContain('Спочатку завантажте'); http.expectNone('/api/auth/antiforgery');
  });
});
