import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subject, catchError, of, startWith, switchMap, tap } from 'rxjs';
import { ApiStatus } from '../../../core/api-status/api-status';
import { CancelAppointment } from '../cancel-appointment/cancel-appointment';
import { AccountShell } from '../account-shell/account-shell';
import { AppointmentCategory, AppointmentSummary, AppointmentsApi, kyivTime, price, statusLabel } from '../appointments-api';

@Component({ selector: 'app-appointment-list', imports: [RouterLink, AccountShell, ApiStatus, CancelAppointment], templateUrl: './appointment-list.html', styleUrl: './appointment-list.scss' })
export class AppointmentList {
  private readonly api = inject(AppointmentsApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly requests = new Subject<AppointmentCategory>();
  readonly category = signal<AppointmentCategory>('all');
  readonly items = signal<AppointmentSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal(false);
  readonly message = signal('');
  readonly statusLabel = statusLabel; readonly kyivTime = kyivTime; readonly price = price;
  readonly filters: { value: AppointmentCategory; label: string }[] = [{ value: 'all', label: 'Усі' }, { value: 'active', label: 'Активні' }, { value: 'completed', label: 'Завершені' }, { value: 'cancelled', label: 'Скасовані' }];
  constructor() {
    this.requests.pipe(startWith('all' as AppointmentCategory), tap(() => { this.loading.set(true); this.error.set(false); this.items.set([]); }),
      switchMap(category => this.api.mine(category).pipe(catchError(() => { this.error.set(true); return of(null); }))),
      takeUntilDestroyed(this.destroyRef)).subscribe(items => { if (items) this.items.set(items); this.loading.set(false); });
  }
  onCancelled() { this.message.set('Запис скасовано.'); this.select(this.category()); }
  select(category: AppointmentCategory) { this.category.set(category); this.requests.next(category); }
}
