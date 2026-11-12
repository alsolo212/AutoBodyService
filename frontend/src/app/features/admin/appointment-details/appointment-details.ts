import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, finalize, of, switchMap, tap } from 'rxjs';
import { AppointmentStatus, price, statusLabel } from '../../appointments/appointments-api';
import { AdminAppointment, AdminAppointmentApi, ContactReason, TransitionChange } from './admin-appointment-api';
import { kyivInput, kyivLocalToUtc, kyivWithOffset } from './kyiv-date';

const contactLabels: Record<ContactReason, string> = {
  NewAppointment: 'Новий запис', ScheduleConflictOrReschedule: 'Конфлікт або перенесення огляду',
  Agreement: 'Узгодження ремонту', ReadyForPayment: 'Готовність до оплати та видачі',
};

@Component({ selector: 'app-admin-appointment-details', imports: [RouterLink, ReactiveFormsModule], templateUrl: './appointment-details.html', styleUrl: './appointment-details.scss' })
export class AppointmentDetailsAdmin {
  private readonly api = inject(AdminAppointmentApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly requests = new Subject<{ id: string; preserveDraft: boolean }>();
  private readonly cancelDialog = viewChild<ElementRef<HTMLDialogElement>>('cancelDialog');
  private id = '';
  private boardActionHandled = false;
  readonly item = signal<AdminAppointment | null>(null);
  readonly loading = signal(true);
  readonly error = signal<'session' | 'forbidden' | 'missing' | 'failed' | null>(null);
  readonly saving = signal(false);
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly conflict = signal(false);
  readonly editingPlan = signal(false);
  readonly copyMessage = signal('');
  readonly copyLocation = signal<'phone' | 'text'>('phone');
  readonly planForm = this.fb.group({
    price: this.fb.control<number | null>(null, [Validators.required, Validators.min(0)]),
    start: ['', Validators.required], startOffset: ['', Validators.required],
    finish: ['', Validators.required], finishOffset: ['', Validators.required], inspectionPerformed: [false],
  });
  readonly transitionForm = this.fb.group({ inspectionPerformed: [false], paymentReceived: [false], vehicleHandedOver: [false] });
  readonly cancelForm = this.fb.group({ reason: ['', [Validators.required, Validators.maxLength(500)]] });
  readonly contactForm = this.fb.group({ reason: this.fb.control<ContactReason>('NewAppointment') });
  readonly planError = signal('');
  readonly cancelError = signal('');
  readonly terminal = computed(() => this.item()?.status === 'Completed' || this.item()?.status === 'Cancelled');
  readonly canCancel = computed(() => ['AwaitingInspection', 'Scheduled', 'InProgress'].includes(this.item()?.status ?? ''));
  readonly nextStatus = computed<AppointmentStatus | null>(() => {
    switch (this.item()?.status) {
      case 'AwaitingInspection': return 'Scheduled';
      case 'Scheduled': return 'InProgress';
      case 'InProgress': return 'AwaitingPayment';
      case 'AwaitingPayment': return 'Completed';
      default: return null;
    }
  });
  readonly format = kyivWithOffset;
  readonly status = statusLabel;
  readonly price = price;
  readonly contactLabels = contactLabels;
  readonly reasons = Object.keys(contactLabels) as ContactReason[];
  readonly suggestedReason = computed<ContactReason>(() => {
    switch (this.item()?.status) {
      case 'Scheduled': return 'Agreement';
      case 'AwaitingPayment': return 'ReadyForPayment';
      default: return 'NewAppointment';
    }
  });
  contactText() { return `Вітаємо! Будь ласка, зв’яжіться з автосервісом щодо вашого замовлення (${contactLabels[this.contactForm.controls.reason.value]}).`; }

  constructor() {
    this.requests.pipe(
      switchMap(request => {
        this.loading.set(true); this.error.set(null); this.item.set(null);
        return this.api.details(request.id).pipe(
        tap(item => {
          this.item.set(item);
          if (!request.preserveDraft) this.resetForms(item);
          if (item.status === 'Completed' || item.status === 'Cancelled') this.editingPlan.set(false);
          this.openBoardAction(item);
        }),
        catchError((error: unknown) => { this.error.set(this.loadError(error)); return of(null); }),
        finalize(() => this.loading.set(false)),
      ); }), takeUntilDestroyed(this.destroyRef),
    ).subscribe();
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      this.id = params.get('id') ?? '';
      this.boardActionHandled = false;
      this.notice.set(''); this.conflict.set(false); this.actionError.set(''); this.editingPlan.set(false); this.saving.set(false);
      this.load();
    });
  }

  load(preserveDraft = false) { if (!this.id) { this.loading.set(false); this.error.set('missing'); return; } this.requests.next({ id: this.id, preserveDraft }); }
  editPlan() { if (this.terminal() || this.saving()) return; this.editingPlan.set(true); this.planError.set(''); }
  closePlan() { this.editingPlan.set(false); this.planError.set(''); if (this.item()) this.fillPlan(this.item()!); }
  savePlan() {
    const item = this.item(); if (!item || this.terminal() || this.saving()) return;
    this.planForm.markAllAsTouched(); this.planError.set('');
    const data = this.planForm.getRawValue();
    const start = kyivLocalToUtc(data.start, data.startOffset), finish = kyivLocalToUtc(data.finish, data.finishOffset);
    if (this.planForm.invalid || data.price === null || !Number.isFinite(data.price) || !start || !finish || Date.parse(finish) < Date.parse(start)) {
      this.planError.set('Перевірте вартість, дати та зміщення UTC. Завершення не може бути раніше початку.'); return;
    }
    if (item.status === 'AwaitingInspection' && !data.inspectionPerformed) { this.planError.set('Підтвердіть, що огляд проведено.'); return; }
    const old = item.repairPlan;
    const unchanged = (value: string, offset: string, original: string | undefined, converted: string) => {
      if (!original) return converted;
      const existing = kyivInput(original);
      const normalized = value.length === 16 ? `${value}:00` : value;
      return existing.local === normalized && existing.offset === offset ? original : converted;
    };
    this.run(this.api.plan(this.id, { version: item.version, price: data.price,
      plannedStartUtc: unchanged(data.start, data.startOffset, old?.plannedStartUtc, start),
      estimatedFinishUtc: unchanged(data.finish, data.finishOffset, old?.estimatedFinishUtc, finish),
      inspectionPerformed: data.inspectionPerformed }), 'Узгоджені дані збережено.');
  }
  advance() {
    const item = this.item(), target = this.nextStatus(); if (!item || !target || this.saving()) return;
    const facts = this.transitionForm.getRawValue();
    if (target === 'Scheduled' && (!item.repairPlan || !facts.inspectionPerformed)) { this.actionError.set('Спочатку збережіть узгоджений план і підтвердьте проведення огляду.'); return; }
    if (target === 'Completed' && (!facts.paymentReceived || !facts.vehicleHandedOver)) { this.actionError.set('Підтвердіть оплату і передачу автомобіля клієнту.'); return; }
    const command: TransitionChange = { version: item.version, target, ...facts };
    this.run(this.api.transition(this.id, command), 'Статус замовлення оновлено.');
  }
  openCancel() { if (!this.canCancel() || this.saving()) return; this.cancelError.set(''); this.cancelDialog()?.nativeElement.showModal(); }
  closeCancel() { this.cancelDialog()?.nativeElement.close(); }
  confirmCancel() {
    const item = this.item(); if (!item || !this.canCancel() || this.saving()) return;
    this.cancelForm.markAllAsTouched(); const reason = this.cancelForm.getRawValue().reason.trim();
    if (this.cancelForm.invalid || !reason) { this.cancelError.set('Вкажіть причину скасування (до 500 символів).'); return; }
    this.closeCancel(); this.run(this.api.cancel(this.id, item.version, reason), 'Замовлення скасовано.');
  }
  markContact() {
    if (!this.item() || this.terminal() || this.saving()) return;
    this.run(this.api.contact(this.id, this.contactForm.getRawValue().reason), 'Ручний контакт зафіксовано.');
  }
  async copy(value: string, location: 'phone' | 'text') {
    this.copyLocation.set(location);
    try { await navigator.clipboard.writeText(value); this.copyMessage.set('Скопійовано.'); }
    catch { this.copyMessage.set('Не вдалося скопіювати. Виділіть текст і скопіюйте вручну.'); }
  }
  private run(action: Observable<void>, message: string) {
    const id = this.id; this.saving.set(true); this.actionError.set(''); this.notice.set(''); this.conflict.set(false);
    action.pipe(finalize(() => { if (this.id === id) this.saving.set(false); }), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { if (this.id !== id) return; this.notice.set(message); this.editingPlan.set(false); this.load(); },
      error: (error: unknown) => {
        if (this.id !== id) return;
        if (error instanceof HttpErrorResponse && error.status === 409) {
          this.conflict.set(true); this.transitionForm.reset(); this.load(true); return;
        }
        if (error instanceof HttpErrorResponse && [401, 403, 404].includes(error.status)) {
          this.item.set(null); this.error.set(this.loadError(error)); return;
        }
        this.actionError.set(error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ заборонено.' :
          error instanceof HttpErrorResponse && error.status === 404 ? 'Замовлення не знайдено.' :
          error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' :
          error instanceof HttpErrorResponse && error.status === 400 ? 'Перевірте введені дані.' : 'Не вдалося виконати дію. Перевірте стан замовлення та з’єднання.');
      },
    });
  }
  private loadError(error: unknown): 'session' | 'forbidden' | 'missing' | 'failed' {
    return error instanceof HttpErrorResponse && error.status === 401 ? 'session' : error instanceof HttpErrorResponse && error.status === 403 ? 'forbidden' : error instanceof HttpErrorResponse && error.status === 404 ? 'missing' : 'failed';
  }
  private openBoardAction(item: AdminAppointment) {
    if (this.boardActionHandled) return;
    const action = this.route.snapshot?.queryParamMap?.get('action');
    if (!action) return;
    this.boardActionHandled = true;
    const version = this.route.snapshot.queryParamMap.get('version');
    if (!version || !/^\d+$/.test(version) || Number(version) !== item.version) { this.conflict.set(true); return; }
    const allowed = (action === 'plan' && item.status === 'AwaitingInspection' && !item.repairPlan)
      || (action === 'schedule' && item.status === 'AwaitingInspection' && !!item.repairPlan)
      || (action === 'complete' && item.status === 'AwaitingPayment')
      || (action === 'cancel' && this.canCancel());
    if (!allowed) { this.conflict.set(true); return; }
    if (action === 'plan') this.editingPlan.set(true);
    const id = this.id;
    afterNextRender(() => {
      if (this.id !== id || this.item()?.version !== item.version) return;
      if (action === 'cancel') this.openCancel();
      else this.document.getElementById(action === 'plan' ? 'plan-price' : 'next-heading')?.focus();
    }, { injector: this.injector });
  }
  private resetForms(item: AdminAppointment) {
    this.fillPlan(item); this.transitionForm.reset(); this.cancelForm.reset(); this.planError.set(''); this.cancelError.set('');
    const reason: ContactReason = item.status === 'Scheduled' ? 'Agreement' : item.status === 'AwaitingPayment' ? 'ReadyForPayment' : 'NewAppointment';
    this.contactForm.setValue({ reason });
  }
  private fillPlan(item: AdminAppointment) {
    const start = item.repairPlan ? kyivInput(item.repairPlan.plannedStartUtc) : { local: '', offset: '' };
    const finish = item.repairPlan ? kyivInput(item.repairPlan.estimatedFinishUtc) : { local: '', offset: '' };
    this.planForm.reset({ price: item.repairPlan?.agreedPrice ?? null, start: start.local, startOffset: start.offset, finish: finish.local, finishOffset: finish.offset, inspectionPerformed: false });
  }
}
