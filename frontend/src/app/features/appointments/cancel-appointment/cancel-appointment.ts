import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { AppointmentStatus, AppointmentsApi } from '../appointments-api';

@Component({ selector: 'app-cancel-appointment', imports: [ConfirmDialog], templateUrl: './cancel-appointment.html', styleUrl: './cancel-appointment.scss' })
export class CancelAppointment {
  readonly id = input.required<string>();
  readonly status = input.required<AppointmentStatus>();
  readonly inspectionStartUtc = input.required<string>();
  readonly isOwn = input.required<boolean>();
  readonly cancelled = output<void>();
  readonly saving = signal(false);
  readonly error = signal('');
  private readonly api = inject(AppointmentsApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = viewChild.required(ConfirmDialog);
  private readonly now = signal(Date.now());
  readonly available = computed(() => this.isOwn() && this.status() === 'AwaitingInspection' && Date.parse(this.inspectionStartUtc()) > this.now());

  constructor() {
    effect(onCleanup => {
      const remaining = Date.parse(this.inspectionStartUtc()) - this.now();
      if (remaining <= 0 || !Number.isFinite(remaining)) return;
      const timer = setTimeout(() => this.now.set(Date.now()), Math.min(remaining + 1, 2_147_483_647));
      onCleanup(() => clearTimeout(timer));
    });
  }
  open() { this.error.set(''); this.dialog().open(); }
  confirm() {
    if (this.saving()) return;
    if (!this.isOwn() || this.status() !== 'AwaitingInspection' || Date.parse(this.inspectionStartUtc()) <= Date.now()) {
      this.now.set(Date.now()); this.error.set('Час огляду вже настав або запис змінився. Оновіть сторінку.'); return;
    }
    this.error.set(''); this.saving.set(true);
    this.api.cancel(this.id()).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.cancelled.emit(),
      error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 409
        ? 'Запис уже змінився або час огляду настав. Оновіть сторінку.'
        : error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403 || error.status === 404)
          ? 'Запис недоступний або сесію завершено. Оновіть сторінку.'
          : 'Не вдалося підтвердити скасування. Оновіть сторінку та перевірте стан запису.'),
    });
  }
}
