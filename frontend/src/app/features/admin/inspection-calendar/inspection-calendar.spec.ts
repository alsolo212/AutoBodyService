import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InspectionCalendar } from './inspection-calendar';

describe('InspectionCalendar', () => {
  let http: HttpTestingController;
  const version = 'a7aa8564-66d2-4758-bdcb-c1177381d199';
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [InspectionCalendar], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function load(date?: string) {
    http.expectOne('/api/admin/inspection-schedule').flush({ version, intervals: [{ dayOfWeek: 'Monday', start: '09:00:00', end: '10:00:00' }] });
    const exception = http.expectOne(request => request.url === '/api/admin/inspection-exceptions');
    const selected = exception.request.params.get('date');
    if (date) expect(selected).toBe(date);
    exception.flush({ version, date: selected, exception: null, bookingConflictsChecked: false, warning: 'Конфлікти не перевірено.' });
    http.expectOne(request => request.url === '/api/inspection-slots' && request.params.get('date') === selected).flush({ date: selected, slots: [{ startUtc: '2026-11-09T07:00:00Z', endUtc: '2026-11-09T07:30:00Z' }] });
  }
  it('loads the selected date and shows only real free slots with the conflict warning', () => {
    const fixture = TestBed.createComponent(InspectionCalendar);
    load(); fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Конфлікти не перевірено.');
    expect(text).toContain('09:00');
    expect(text).toContain('Вільно');
    expect(text).toContain('Список записів поки недоступний');
    expect(fixture.componentInstance.weeklyIntervals.at(0).getRawValue()).toEqual({ dayOfWeek: 'Monday', start: '09:00', end: '10:00' });
  });
  it('sends versioned exceptions with antiforgery and reports conflicts without erasing edits', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load(); fixture.detectChanges();
    const component = fixture.componentInstance;
    component.setTab('settings'); component.exceptionForm.controls.mode.setValue('Closed');
    (fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).showModal = () => {};
    component.requestSave('date');
    // Invoke the confirmed action without relying on dialog support in jsdom.
    component.save();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const request = http.expectOne('/api/admin/inspection-exceptions');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.version).toBe(version);
    expect(request.request.body.exception).toEqual({ mode: 'Closed', reason: null, intervals: [], slots: [] });
    request.flush({}, { status: 409, statusText: 'Conflict' });
    expect(component.error()).toContain('Графік змінився');
    expect(component.exceptionForm.controls.mode.value).toBe('Closed');
  });
  it('does not save to a date that has not been loaded', () => {
    const fixture = TestBed.createComponent(InspectionCalendar); load(); fixture.detectChanges();
    const component = fixture.componentInstance;
    component.dateForm.controls.date.setValue('2026-11-30');
    component.requestSave('weekly');
    expect(component.error()).toContain('Спочатку завантажте');
    http.expectNone('/api/auth/antiforgery');
  });
});
