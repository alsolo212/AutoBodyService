import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { statusLabel } from '../../appointments/appointments-api';
import { AdminAppointmentApi, AdminDashboard, ContactReason, ContactTask } from '../appointment-details/admin-appointment-api';
import { kyivWithOffset } from '../appointment-details/kyiv-date';

const reasons: Record<ContactReason, string> = {
  NewAppointment: 'Нова заявка', ScheduleConflictOrReschedule: 'Конфлікт або перенесення огляду',
  Agreement: 'Узгодження ремонту', ReadyForPayment: 'Автомобіль готовий до оплати',
};

@Component({ selector: 'app-admin-dashboard', imports: [RouterLink], templateUrl: './dashboard.html', styleUrl: './dashboard.scss' })
export class Dashboard {
  private readonly api = inject(AdminAppointmentApi);
  private readonly destroyRef = inject(DestroyRef);
  readonly data = signal<AdminDashboard | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly contactError = signal('');
  readonly notice = signal('');
  readonly saving = signal(false);
  readonly format = kyivWithOffset;
  readonly statusLabel = statusLabel;
  reason(reason: ContactReason) { return reasons[reason] ?? 'Ручний контакт'; }

  constructor() { this.reload(); }
  reload() {
    if (this.loading()) return;
    this.loading.set(true); this.error.set('');
    this.api.dashboard().pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: data => this.data.set(data),
      error: (error: unknown) => {
        this.data.set(null);
        this.error.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' :
          error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ до панелі заборонено.' : 'Не вдалося завантажити огляд. Спробуйте ще раз.');
      },
    });
  }
  markContact(task: ContactTask) {
    if (this.saving() || this.loading()) return;
    this.saving.set(true); this.notice.set(''); this.contactError.set('');
    this.api.contact(task.appointmentId, task.reason).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.notice.set('Ручний контакт відмічено.'); this.reload(); },
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 409) {
          this.contactError.set('Замовлення змінилося. Перевірте актуальний стан перед наступною дією.');
          this.reload();
          return;
        }
        this.contactError.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' :
          error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ до дії заборонено.' :
          error instanceof HttpErrorResponse && error.status === 404 ? 'Замовлення не знайдено. Оновіть огляд.' : 'Не вдалося зберегти відмітку. Спробуйте ще раз.');
      },
    });
  }
}
