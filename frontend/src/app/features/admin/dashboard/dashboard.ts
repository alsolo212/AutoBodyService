import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { statusLabel } from '../../appointments/appointments-api';
import { AdminAppointmentApi, AdminDashboard, ContactReason, ContactTask } from '../appointment-details/admin-appointment-api';
import { kyivWithOffset } from '../appointment-details/kyiv-date';
import { ManualContactDialog } from '../manual-contact-dialog/manual-contact-dialog';
import { RepairOffer } from '../repair-offer/repair-offer';

const reasons: Record<ContactReason, string> = {
  NewAppointment: 'Нова заявка', ScheduleConflictOrReschedule: 'Конфлікт або перенесення огляду',
  Agreement: 'Узгодження ремонту', ReadyForPayment: 'Автомобіль готовий до оплати',
  RepairDateConflict: 'Конфлікт дати ремонту',
};

@Component({ selector: 'app-admin-dashboard', imports: [RouterLink, ManualContactDialog, RepairOffer], templateUrl: './dashboard.html', styleUrl: './dashboard.scss' })
export class Dashboard {
  private readonly api = inject(AdminAppointmentApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly contactDialog = viewChild.required(ManualContactDialog);
  readonly data = signal<AdminDashboard | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
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
    if (this.loading()) return;
    this.notice.set('');
    this.contactDialog().open(task.appointmentId, task.reason, task, true);
  }
  contactRecorded() { this.notice.set('Ручний контакт відмічено.'); this.reload(); }
}
