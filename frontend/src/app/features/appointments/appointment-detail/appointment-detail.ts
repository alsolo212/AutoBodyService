import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subject, catchError, combineLatest, of, startWith, switchMap, tap } from 'rxjs';
import { AccountShell } from '../account-shell/account-shell';
import { AppointmentDetails, AppointmentsApi, kyivTime, price, statusLabel } from '../appointments-api';

@Component({ selector: 'app-appointment-detail', imports: [RouterLink, AccountShell], templateUrl: './appointment-detail.html', styleUrl: './appointment-detail.scss' })
export class AppointmentDetail {
  private readonly api = inject(AppointmentsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly retry = new Subject<void>();
  readonly item = signal<AppointmentDetails | null>(null);
  readonly loading = signal(true);
  readonly error = signal<'missing' | 'session' | 'failed' | null>(null);
  readonly statusLabel = statusLabel; readonly kyivTime = kyivTime; readonly price = price;
  constructor() {
    combineLatest([this.route.paramMap, this.retry.pipe(startWith(undefined))]).pipe(
      tap(() => { this.item.set(null); this.error.set(null); this.loading.set(true); }),
      switchMap(([params]) => this.api.details(params.get('id') ?? '').pipe(catchError((error: unknown) => {
        this.error.set(error instanceof HttpErrorResponse && error.status === 404 ? 'missing' : error instanceof HttpErrorResponse && error.status === 401 ? 'session' : 'failed');
        return of(null);
      }))), takeUntilDestroyed(this.destroyRef),
    ).subscribe(item => { this.item.set(item); this.loading.set(false); });
  }
  reload() { this.retry.next(); }
}
