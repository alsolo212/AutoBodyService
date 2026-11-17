import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, of, switchMap } from 'rxjs';

interface DayAvailability { date: string; available: boolean; }
interface MonthResponse { days: DayAvailability[]; }
interface Request { month: string; search: boolean; remaining: number; focus?: string; }
interface CalendarDay { date: string; number: number; status: 'past' | 'available' | 'unavailable' | 'loading' | 'unknown'; label: string; }

const dateOf = (value: string) => new Date(`${value}T12:00:00Z`);
const dateString = (date: Date) => date.toISOString().slice(0, 10);
const addDays = (value: string, count: number) => { const date = dateOf(value); date.setUTCDate(date.getUTCDate() + count); return dateString(date); };
const addMonths = (value: string, count: number) => { const date = dateOf(`${value.slice(0, 7)}-01`); date.setUTCMonth(date.getUTCMonth() + count); return dateString(date).slice(0, 7); };
const dayFormat = new Intl.DateTimeFormat('uk-UA', { timeZone: 'UTC', dateStyle: 'full' });
const monthFormat = new Intl.DateTimeFormat('uk-UA', { timeZone: 'UTC', month: 'long', year: 'numeric' });

@Component({ selector: 'app-availability-calendar', templateUrl: './availability-calendar.html', styleUrl: './availability-calendar.scss' })
export class AvailabilityCalendar implements OnInit, OnChanges {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly requests = new Subject<Request>();
  private initialized = false;
  @Input({ required: true }) today = '';
  @Input() selectedDate = '';
  @Input() revision = 0;
  @Output() readonly selectedDateChange = new EventEmitter<string>();
  readonly month = signal('');
  readonly focused = signal('');
  readonly days = signal<CalendarDay[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);
  readonly searchExhausted = signal(false);
  readonly searching = signal(false);
  readonly weekdays = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Нд'];
  private lastRequest: Request | null = null;

  constructor() {
    this.requests.pipe(switchMap(request => {
      this.lastRequest = request;
      this.month.set(request.month); this.loading.set(true); this.searching.set(request.search); this.error.set(false);
      this.makeDays(request.month, []);
      const [year, month] = request.month.split('-');
      return this.http.get<MonthResponse>('/api/inspection-slots/month', { params: { year, month } }).pipe(
        switchMap(response => {
          this.makeDays(request.month, response.days);
          const nearest = response.days.find(day => day.date >= this.today && day.available);
          if (request.search && !nearest && request.remaining > 1 && request.month < '9998-12') {
            const next: Request = { month: addMonths(request.month, 1), search: true, remaining: request.remaining - 1 };
            queueMicrotask(() => { if (this.lastRequest === request) this.requests.next(next); });
          } else {
            this.loading.set(false); this.searching.set(false);
            this.searchExhausted.set(request.search && !nearest);
            if (request.search && nearest) this.select(nearest.date);
            if (request.focus) requestAnimationFrame(() => { if (this.month() === request.month) this.focusDate(request.focus!); });
          }
          return of(null);
        }),
        catchError(() => { this.makeDays(request.month, [], true); this.loading.set(false); this.searching.set(false); this.error.set(true); return of(null); }),
      );
    }), takeUntilDestroyed(this.destroyRef)).subscribe();
  }
  ngOnInit(): void {
    this.initialized = true;
    const date = this.selectedDate || this.today;
    this.focused.set(date);
    this.load({ month: date.slice(0, 7), search: !this.selectedDate, remaining: 12 });
  }
  ngOnChanges(changes: SimpleChanges): void {
    if (this.initialized && changes['revision'] && !changes['revision'].firstChange) {
      this.load({ month: this.selectedDate?.slice(0, 7) || this.month(), search: false, remaining: 0 });
    } else if (this.initialized && changes['selectedDate'] && this.selectedDate && (this.selectedDate.slice(0, 7) !== this.month() || this.searching())) {
      this.focused.set(this.selectedDate);
      this.load({ month: this.selectedDate.slice(0, 7), search: false, remaining: 0 });
    }
  }
  private load(request: Request): void { this.searchExhausted.set(false); this.requests.next(request); }
  retry(): void { if (this.lastRequest) this.load(this.lastRequest); }
  searchMore(): void { this.load({ month: addMonths(this.month(), 1), search: true, remaining: 12 }); }
  moveMonth(delta: number): void {
    const next = addMonths(this.month(), delta);
    if (next < this.today.slice(0, 7) || next > '9998-12') return;
    this.focused.set(`${next}-01`);
    this.load({ month: next, search: false, remaining: 0 });
  }
  get monthLabel(): string { return monthFormat.format(dateOf(`${this.month()}-01`)); }
  get leadingDays(): number[] { return Array((dateOf(`${this.month()}-01`).getUTCDay() + 6) % 7).fill(0); }
  private makeDays(month: string, availability: DayAvailability[], failed = false): void {
    const states = new Map(availability.map(day => [day.date, day.available]));
    const first = dateOf(`${month}-01`);
    const count = new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 1, 0)).getUTCDate();
    this.days.set(Array.from({ length: count }, (_, index) => {
      const date = `${month}-${String(index + 1).padStart(2, '0')}`;
      const status = date < this.today ? 'past' : !states.has(date) ? failed ? 'unknown' : 'loading' : states.get(date) ? 'available' : 'unavailable';
      const description = { past: 'минула дата', available: 'є вільний час', unavailable: 'немає вільного часу', loading: 'доступність завантажується', unknown: 'доступність невідома' }[status];
      return { date, number: index + 1, status, label: `${dayFormat.format(dateOf(date))}, ${description}` };
    }));
  }
  select(date: string): void {
    const day = this.days().find(item => item.date === date);
    if (!day || day.status !== 'available') return;
    this.focused.set(date); this.selectedDateChange.emit(date);
  }
  onKey(event: KeyboardEvent, date: string): void {
    const offset = ({ ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 } as Record<string, number>)[event.key];
    if (offset === undefined && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    const weekday = (dateOf(date).getUTCDay() + 6) % 7;
    const target = addDays(date, offset ?? (event.key === 'Home' ? -weekday : 6 - weekday));
    if (target < `${this.today.slice(0, 7)}-01` || target > '9998-12-31') return;
    this.focused.set(target);
    if (target.slice(0, 7) !== this.month()) this.load({ month: target.slice(0, 7), search: false, remaining: 0, focus: target });
    else this.focusDate(target);
  }
  private focusDate(date: string): void { document.getElementById(`booking-day-${date}`)?.focus(); }
}
