import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, Input, inject, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { AdminAppointment, AdminAppointmentApi, ContactReason } from '../appointment-details/admin-appointment-api';
import { kyivDate } from '../../../shared/time/kyiv-time';

const labels: Record<ContactReason, string> = {
  NewAppointment: 'Нова заявка', ScheduleConflictOrReschedule: 'Конфлікт або перенесення огляду',
  Agreement: 'Узгодження ремонту', ReadyForPayment: 'Автомобіль готовий до оплати',
  RepairDateConflict: 'Конфлікт дати ремонту',
};
const prompts: Record<ContactReason, string> = {
  NewAppointment: 'щодо запису на огляд', ScheduleConflictOrReschedule: 'щодо зміни часу огляду',
  Agreement: 'щодо узгодження ремонту', ReadyForPayment: 'щодо оплати та видачі автомобіля',
  RepairDateConflict: 'щодо перенесення запланованого ремонту',
};

@Component({ selector: 'app-manual-contact-dialog', templateUrl: './manual-contact-dialog.html', styleUrl: './manual-contact-dialog.scss' })
export class ManualContactDialog {
  @Input() dialogId = 'manual-contact';
  private readonly api = inject(AdminAppointmentApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private serial = 0;
  readonly recorded = output<void>();
  readonly loading = signal(false); readonly saving = signal(false);
  readonly item = signal<AdminAppointment | null>(null); readonly error = signal(''); readonly copied = signal('');
  readonly reason = signal<ContactReason>('NewAppointment');
  readonly labels = labels;
  readonly kyivDate = kyivDate;

  open(id: string, reason: ContactReason, expected?: { customerName: string; make: string; model: string }, checkTaskStatus = false) {
    if (this.saving()) return;
    const serial = ++this.serial;
    this.item.set(null); this.error.set(''); this.copied.set(''); this.reason.set(reason); this.loading.set(true);
    this.dialog().nativeElement.showModal();
    this.api.details(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: item => {
        if (serial !== this.serial) return;
        this.loading.set(false);
        if (expected && (item.customerName !== expected.customerName || item.make !== expected.make || item.model !== expected.model)) {
          this.error.set('Дані клієнта або автомобіля змінилися. Оновіть замовлення перед зв’язком.'); return;
        }
        const expectedStatus: Partial<Record<ContactReason, string>> = { NewAppointment: 'AwaitingInspection', Agreement: 'Scheduled', ReadyForPayment: 'AwaitingPayment' };
        if (checkTaskStatus && expectedStatus[reason] && item.status !== expectedStatus[reason]) {
          this.error.set('Стан замовлення змінився. Оновіть список завдань перед зв’язком.'); return;
        }
        if (!item.customerPhone?.trim() || ['Completed', 'Cancelled'].includes(item.status)) {
          this.error.set('Телефон недоступний або замовлення вже завершено. Оновіть дані.'); return;
        }
        this.item.set(item);
      },
      error: (error: unknown) => { if (serial === this.serial) { this.loading.set(false); this.error.set(error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ заборонено або телефон клієнта недоступний. Оновіть дані.' : 'Не вдалося завантажити актуальні дані клієнта.'); } },
    });
  }
  message(item: AdminAppointment) { return `Вітаємо! Будь ласка, зв’яжіться з автосервісом ${prompts[this.reason()]} автомобіля ${item.make} ${item.model}.`; }
  close() { if (!this.saving()) this.dialog().nativeElement.close(); }
  closed() { this.serial++; this.item.set(null); }
  cancel(event: Event) { if (this.saving()) event.preventDefault(); }
  async copy(value: string) {
    try { await navigator.clipboard.writeText(value); this.copied.set('Скопійовано.'); }
    catch { this.copied.set('Не вдалося скопіювати. Виділіть текст і скопіюйте вручну.'); }
  }
  confirm() {
    const item = this.item(); if (!item || this.loading() || this.saving()) return;
    this.saving.set(true); this.error.set('');
    this.api.contact(item.id, this.reason(), { version: item.version, customerPhone: item.customerPhone, customerName: item.customerName })
      .pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
        next: () => { this.saving.set(false); this.close(); this.recorded.emit(); },
        error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 409
          ? 'Дані або стан замовлення змінилися. Закрийте вікно й відкрийте актуальні дані.'
          : 'Не вдалося зберегти відмітку. Перевірте доступ і з’єднання.'),
      });
  }
}
