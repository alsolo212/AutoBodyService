import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AvailabilityCalendar } from './availability-calendar';

describe('AvailabilityCalendar', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AvailabilityCalendar], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function create(selectedDate = '') {
    const fixture = TestBed.createComponent(AvailabilityCalendar);
    fixture.componentRef.setInput('today', '2026-11-17');
    fixture.componentRef.setInput('selectedDate', selectedDate);
    fixture.detectChanges();
    return fixture;
  }
  function respond(month: string, available: string[] = []) {
    const [year, number] = month.split('-').map(Number);
    const count = new Date(Date.UTC(year, number, 0)).getUTCDate();
    http.expectOne(request => request.url === '/api/inspection-slots/month' && request.params.get('month') === String(number).padStart(2, '0')).flush({ days: Array.from({ length: count }, (_, index) => {
      const date = `${month}-${String(index + 1).padStart(2, '0')}`;
      return { date, available: available.includes(date) };
    }) });
  }
  it('finds the nearest available date across month boundaries', async () => {
    const fixture = create(); const selected: string[] = [];
    fixture.componentInstance.selectedDateChange.subscribe(value => selected.push(value));
    respond('2026-11'); await Promise.resolve();
    respond('2026-12', ['2026-12-03', '2026-12-08']); fixture.detectChanges();
    expect(selected).toEqual(['2026-12-03']);
    expect(fixture.componentInstance.month()).toBe('2026-12');
  });
  it('keeps a manually entered date when the initial search is still loading', () => {
    const fixture = create(); const selected: string[] = [];
    fixture.componentInstance.selectedDateChange.subscribe(value => selected.push(value));
    const pending = http.expectOne(request => request.url.endsWith('/month'));
    fixture.componentRef.setInput('selectedDate', '2026-11-25'); fixture.detectChanges();
    expect(pending.cancelled).toBe(true);
    respond('2026-11', ['2026-11-18', '2026-11-25']); fixture.detectChanges();
    expect(selected).toEqual([]);
    expect(fixture.componentInstance.selectedDate).toBe('2026-11-25');
  });
  it('does not request a month outside the booking horizon for manual input', () => {
    const fixture = create('2026-11-18'); respond('2026-11', ['2026-11-18']);
    fixture.componentRef.setInput('selectedDate', '2027-01-01'); fixture.detectChanges();
    expect(fixture.componentInstance.month()).toBe('2026-11');
    http.expectNone(request => request.url.endsWith('/month'));
  });
  it('describes past, unavailable and available days and selects only available dates', () => {
    const fixture = create('2026-11-18');
    const selected: string[] = []; fixture.componentInstance.selectedDateChange.subscribe(value => selected.push(value));
    respond('2026-11', ['2026-11-18']); fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('#booking-day-2026-11-16')?.getAttribute('aria-label')).toContain('минула дата');
    expect(el.querySelector('#booking-day-2026-11-17')?.getAttribute('aria-label')).toContain('немає вільного часу');
    expect(el.querySelector('#booking-day-2026-11-18')?.getAttribute('aria-label')).toContain('є вільний час');
    (el.querySelector('#booking-day-2026-11-17') as HTMLButtonElement).click();
    (el.querySelector('#booking-day-2026-11-18') as HTMLButtonElement).click();
    expect(selected).toEqual(['2026-11-18']);
  });
  it('moves keyboard focus across months and activates a free day', async () => {
    const fixture = create('2026-11-30'); respond('2026-11', ['2026-11-30']); fixture.detectChanges();
    const component = fixture.componentInstance; const selected: string[] = [];
    component.selectedDateChange.subscribe(value => selected.push(value));
    const button = fixture.nativeElement.querySelector('#booking-day-2026-11-30') as HTMLButtonElement;
    const event = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    button.dispatchEvent(event); expect(event.defaultPrevented).toBe(true);
    respond('2026-12', ['2026-12-01']); fixture.detectChanges(); await Promise.resolve();
    const target = fixture.nativeElement.querySelector('#booking-day-2026-12-01') as HTMLButtonElement;
    expect(target.tabIndex).toBe(0); target.click(); expect(selected).toEqual(['2026-12-01']);
  });
  it('shows errors and retries the same month without inventing availability', () => {
    const fixture = create('2026-11-18');
    http.expectOne(request => request.url.endsWith('/month')).flush({}, { status: 503, statusText: 'Unavailable' }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити');
    expect(fixture.componentInstance.days().find(day => day.date === '2026-11-18')?.status).toBe('unknown');
    fixture.componentInstance.retry(); respond('2026-11', ['2026-11-18']); fixture.detectChanges();
    expect(fixture.componentInstance.days().find(day => day.date === '2026-11-18')?.status).toBe('available');
  });
  it('stops after next month and returns to the current month with contacts', async () => {
    const fixture = create();
    respond('2026-11'); await Promise.resolve(); respond('2026-12'); await Promise.resolve();
    respond('2026-11'); fixture.detectChanges();
    http.expectOne('/api/public/contacts').flush({ phone: '+380000000000', address: 'Тестова адреса', workingHours: null }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('До кінця наступного місяця вільних дат немає');
    expect(fixture.nativeElement.textContent).toContain('+380000000000');
    expect(fixture.componentInstance.month()).toBe('2026-11');
    fixture.componentInstance.moveMonth(1); respond('2026-12'); fixture.componentInstance.moveMonth(1);
    http.expectNone(request => request.params.get('month') === '01');
  });
});
