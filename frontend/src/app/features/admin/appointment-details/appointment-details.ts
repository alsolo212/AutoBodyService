import { AdminShell } from '../../../layout/admin-shell/admin-shell';
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, Subject, catchError, debounceTime, finalize, of, switchMap, tap } from 'rxjs';
import { AppointmentStatus, price, statusLabel } from '../../appointments/appointments-api';
import { AdminAppointment, AdminAppointmentApi, ContactReason, TransitionChange } from './admin-appointment-api';
import { decimalLiteral } from '../../../shared/money/money';
import { kyivInput, kyivLocalToUtc, kyivOffsets, kyivWithOffset } from './kyiv-date';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { RepairCapacityApi, RepairPreview } from '../repair-capacity-api';
import { kyivDate } from '../../../shared/time/kyiv-time';
import { ManualContactDialog } from '../manual-contact-dialog/manual-contact-dialog';
import { RepairOffer } from '../repair-offer/repair-offer';
import { DelayPreview, RepairPlanningApi } from '../repair-planning-api';

const exactPrice: ValidatorFn = control => decimalLiteral(String(control.value ?? '')) === null ? { invalidAmount: true } : null;

const contactLabels: Record<ContactReason, string> = {
  NewAppointment: 'Новий запис', ScheduleConflictOrReschedule: 'Конфлікт або перенесення огляду',
  Agreement: 'Узгодження ремонту', ReadyForPayment: 'Готовність до оплати та видачі',
  RepairDateConflict: 'Конфлікт дати ремонту',
};

@Component({ selector: 'app-admin-appointment-details', imports: [RouterLink, ReactiveFormsModule, AdminShell, ConfirmDialog, ManualContactDialog, RepairOffer], templateUrl: './appointment-details.html', styleUrl: './appointment-details.scss' })
export class AppointmentDetailsAdmin {
  private readonly api = inject(AdminAppointmentApi);
  private readonly capacityApi = inject(RepairCapacityApi);
  private readonly planningApi = inject(RepairPlanningApi);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly requests = new Subject<{ id: string; preserveDraft: boolean }>();
  private readonly cancelDialog = viewChild<ElementRef<HTMLDialogElement>>('cancelDialog');
  private readonly transitionDialog = viewChild<ConfirmDialog>('transitionDialog');
  private readonly contactDialog = viewChild.required(ManualContactDialog);
  private id = '';
  private boardActionHandled = false;
  readonly item = signal<AdminAppointment | null>(null);
  readonly loading = signal(true);
  readonly error = signal<'session' | 'forbidden' | 'missing' | 'failed' | null>(null);
  readonly saving = signal(false);
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly conflict = signal(false);
  readonly pendingTransition = signal<{ id: string; version: number; target: AppointmentStatus } | null>(null);
  readonly editingPlan = signal(false);
  readonly copyMessage = signal('');
  readonly copyLocation = signal<'phone' | 'text'>('phone');
  readonly planForm = this.fb.group({
    price: ['', exactPrice],
    start: ['', Validators.required], startOffset: [''],
    finish: ['', Validators.required], finishOffset: [''], inspectionPerformed: [false],
  });
  readonly scheduleForm = this.fb.group({ start: ['', Validators.required], durationDays: [1, [Validators.required, Validators.min(1)]], price: ['', exactPrice], inspectionPerformed: [false] });
  readonly availability = signal<RepairPreview | null>(null);
  readonly availabilityError = signal('');
  private readonly previewRequests = new Subject<{ start: string; duration: number; recommend: boolean }>();
  readonly transitionForm = this.fb.group({ inspectionPerformed: [false], paymentReceived: [false], vehicleHandedOver: [false] });
  readonly cancelForm = this.fb.group({ reason: ['', [Validators.required, Validators.maxLength(500)]] });
  readonly pauseForm = this.fb.group({ reason: ['', [Validators.required, Validators.maxLength(500)]], durationDays: [1, [Validators.required, Validators.min(1)]], workplaceReleased: [false] });
  readonly customerAgreed = this.fb.control(false);
  readonly editingPause = signal(false);
  readonly delayFinish = this.fb.control('', Validators.required);
  readonly delayPreview = signal<DelayPreview | null>(null);
  readonly delayLoading = signal(false);
  readonly contactForm = this.fb.group({ reason: this.fb.control<ContactReason>('NewAppointment') });
  readonly planError = signal('');
  readonly cancelError = signal('');
  readonly terminal = computed(() => this.item()?.status === 'Completed' || this.item()?.status === 'Cancelled');
  readonly canCancel = computed(() => ['AwaitingInspection', 'Scheduled', 'InProgress', 'Paused'].includes(this.item()?.status ?? ''));
  readonly nextStatus = computed<AppointmentStatus | null>(() => {
    switch (this.item()?.status) {
      case 'AwaitingInspection': return 'Scheduled';
      case 'Scheduled': return 'InProgress';
      case 'Paused': return 'InProgress';
      case 'InProgress': return 'AwaitingPayment';
      case 'AwaitingPayment': return 'Completed';
      default: return null;
    }
  });
  readonly format = kyivWithOffset;
  readonly kyivDate = kyivDate;
  readonly offsets = kyivOffsets;
  readonly status = statusLabel;
  readonly price = price;
  readonly contactLabels = contactLabels;
  readonly reasons = Object.keys(contactLabels) as ContactReason[];
  readonly suggestedReason = computed<ContactReason>(() => {
    switch (this.item()?.status) {
      case 'Scheduled': case 'InProgress': return 'Agreement';
      case 'AwaitingPayment': return 'ReadyForPayment';
      default: return 'NewAppointment';
    }
  });
  contactText() {
    const item = this.item();
    const car = item ? ` ${item.make} ${item.model}` : '';
    const message: Record<ContactReason, string> = {
      NewAppointment: `щодо запису на огляд автомобіля${car}`,
      ScheduleConflictOrReschedule: `щодо узгодження часу огляду автомобіля${car}`,
      Agreement: `щодо узгодження ремонту автомобіля${car}`,
      ReadyForPayment: `щодо оплати та видачі автомобіля${car}`,
      RepairDateConflict: `щодо перенесення ремонту автомобіля${car}`,
    };
    const reason = this.contactForm.controls.reason.value;
    return `Вітаємо! Будь ласка, зв’яжіться з автосервісом ${message[reason]}.`;
  }

  constructor() {
    this.previewRequests.pipe(debounceTime(150), switchMap(request => this.capacityApi.preview(request.start, request.duration).pipe(
      tap(result => {
        const current = this.scheduleForm.getRawValue();
        if (current.start !== request.start || current.durationDays !== request.duration) return;
        this.availability.set(result); this.availabilityError.set('');
        if (request.recommend && result.nearestStart && result.nearestStart !== current.start) this.scheduleForm.controls.start.setValue(result.nearestStart);
      }), catchError(() => { this.availability.set(null); this.availabilityError.set('Не вдалося перевірити доступність. Перевірте дату та тривалість.'); return of(null); }),
    )), takeUntilDestroyed(this.destroyRef)).subscribe();
    this.scheduleForm.controls.start.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.previewSchedule());
    this.scheduleForm.controls.durationDays.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.previewSchedule());
    this.planForm.controls.start.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.planForm.controls.startOffset.setValue(''));
    this.planForm.controls.finish.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.planForm.controls.finishOffset.setValue(''));
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
  editPlan() {
    if (this.terminal() || this.saving()) return;
    this.editingPlan.set(true); this.planError.set(''); this.customerAgreed.setValue(false);
    if (this.item()?.status === 'AwaitingInspection') {
      const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
      this.scheduleForm.reset({ start: today, durationDays: 1, price: this.item()?.repairPlan?.agreedPriceExact ?? '', inspectionPerformed: false }, { emitEvent: false });
      this.availability.set(null); this.previewRequests.next({ start: today, duration: 1, recommend: true });
    }
  }
  previewSchedule() {
    const { start, durationDays } = this.scheduleForm.getRawValue();
    this.availability.set(null);
    if (start && Number.isSafeInteger(durationDays) && durationDays > 0) {
      this.availabilityError.set(''); this.previewRequests.next({ start, duration: durationDays, recommend: false });
    } else this.availabilityError.set('Оберіть дату та додатну цілу тривалість.');
  }
  useNearest() { const date = this.availability()?.nearestStart; if (date) this.scheduleForm.controls.start.setValue(date); }
  schedule() {
    const item = this.item(); if (!item || this.saving()) return;
    this.scheduleForm.markAllAsTouched();
    const data = this.scheduleForm.getRawValue(); const amount = decimalLiteral(data.price);
    if (this.scheduleForm.invalid || !Number.isSafeInteger(data.durationDays) || !amount || !data.inspectionPerformed || !this.availability()?.fits) {
      this.planError.set('Перевірте доступність, тривалість, вартість і підтвердження огляду.'); return;
    }
    this.planError.set('');
    this.run(this.api.schedule(this.id, { ...data, version: item.version, price: amount }), 'Ремонт заплановано.');
  }
  pauseRepair() {
    const item = this.item(); if (!item || item.status !== 'InProgress' || this.saving()) return;
    const value = this.pauseForm.getRawValue(); this.pauseForm.markAllAsTouched();
    if (this.pauseForm.invalid || !value.reason.trim() || !Number.isSafeInteger(value.durationDays)) { this.actionError.set('Вкажіть причину та додатну кількість календарних днів.'); return; }
    this.editingPause.set(false);
    this.run(this.api.pause(this.id, { ...value, reason: value.reason.trim(), version: item.version }), 'Ремонт призупинено.');
  }
  previewDelay() {
    const finish = this.delayFinish.value, id = this.id;
    if (!finish || this.delayLoading() || this.saving()) return;
    this.delayPreview.set(null); this.actionError.set(''); this.delayLoading.set(true);
    this.planningApi.delayPreview(id, finish).pipe(finalize(() => this.delayLoading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => { if (this.id === id && this.delayFinish.value === finish) this.delayPreview.set(result); },
      error: () => this.actionError.set('Не вдалося перевірити затримку. Нова дата має бути пізнішою.'),
    });
  }
  saveDelay() {
    const item = this.item(), preview = this.delayPreview();
    if (!item || !preview || preview.finish !== this.delayFinish.value || this.saving()) return;
    this.run(this.planningApi.delay(this.id, item.version, preview), 'Затримку збережено. Перевірте конфлікти та зв’яжіться з клієнтами.');
    this.delayPreview.set(null);
  }
  closePlan() { this.editingPlan.set(false); this.planError.set(''); if (this.item()) this.fillPlan(this.item()!); }
  savePlan() {
    const item = this.item(); if (!item || item.status === 'AwaitingInspection' || this.terminal() || this.saving()) return;
    this.planForm.markAllAsTouched(); this.planError.set('');
    const data = this.planForm.getRawValue();
    const startOffsets = kyivOffsets(data.start), finishOffsets = kyivOffsets(data.finish);
    const startOffset = startOffsets.length === 1 ? startOffsets[0] : data.startOffset;
    const finishOffset = finishOffsets.length === 1 ? finishOffsets[0] : data.finishOffset;
    const start = kyivLocalToUtc(data.start, startOffset), finish = kyivLocalToUtc(data.finish, finishOffset);
    const amount = decimalLiteral(data.price);
    if (amount === null) { this.planError.set('Вкажіть невід’ємну вартість, яку можна точно зберегти. Перевірте кількість цифр.'); return; }
    if (this.planForm.invalid || !start || !finish || Date.parse(finish) < Date.parse(start)) {
      this.planError.set('Перевірте дату й час за Києвом. Для повторного часу оберіть варіант; завершення не може бути раніше початку.'); return;
    }
    const old = item.repairPlan;
    const unchanged = (value: string, offset: string, original: string | undefined, converted: string) => {
      if (!original) return converted;
      const existing = kyivInput(original);
      const normalized = value.length === 16 ? `${value}:00` : value;
      return existing.local === normalized && existing.offset === offset ? original : converted;
    };
    const plannedStartUtc = unchanged(data.start, startOffset, old?.plannedStartUtc, start);
    const estimatedFinishUtc = unchanged(data.finish, finishOffset, old?.estimatedFinishUtc, finish);
    const datesChanged = !!old && (Date.parse(plannedStartUtc) !== Date.parse(old.plannedStartUtc) || Date.parse(estimatedFinishUtc) !== Date.parse(old.estimatedFinishUtc));
    if (item.status === 'Scheduled' && datesChanged && !this.customerAgreed.value) { this.planError.set('Підтвердіть, що нові дати погоджено з клієнтом.'); return; }
    this.run(this.api.plan(this.id, { version: item.version, price: amount,
      plannedStartUtc, estimatedFinishUtc,
      inspectionPerformed: data.inspectionPerformed, ...(item.status === 'Scheduled' && datesChanged ? { customerAgreed: true } : {}) }), 'Узгоджені дані збережено.');
  }
  advance() {
    const item = this.item(), target = this.nextStatus(); if (!item || !target || this.saving()) return;
    const facts = this.transitionForm.getRawValue();
    if (target === 'Scheduled') { this.editPlan(); return; }
    if (target === 'Completed' && (!facts.paymentReceived || !facts.vehicleHandedOver)) { this.actionError.set('Підтвердіть оплату і передачу автомобіля клієнту.'); return; }
    const command: TransitionChange = { version: item.version, target, ...facts };
    this.run(item.status === 'Paused' ? this.api.resume(this.id, item.version) : this.api.transition(this.id, command), 'Статус замовлення оновлено.');
  }
  requestAdvance() {
    const target = this.nextStatus(), item = this.item();
    if (!item || !target || this.saving()) return;
    if (target === 'InProgress' || target === 'AwaitingPayment') {
      this.pendingTransition.set({ id: this.id, version: item.version, target });
      this.transitionDialog()?.open();
    }
    else this.advance();
  }
  confirmAdvance() {
    const pending = this.pendingTransition(); this.pendingTransition.set(null);
    if (!pending || this.id !== pending.id || this.item()?.version !== pending.version || this.nextStatus() !== pending.target) {
      this.actionError.set('Замовлення змінилося. Перевірте актуальний стан.'); return;
    }
    this.advance();
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
    this.contactDialog().open(this.id, this.contactForm.getRawValue().reason, this.item()!);
  }
  contactRecorded() { this.notice.set('Ручний контакт зафіксовано.'); this.load(true); }
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
          this.conflict.set(true); this.transitionForm.reset(); this.load(true);
          if (this.editingPlan()) this.previewSchedule();
          return;
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
      || (action === 'resume' && item.status === 'Paused')
      || (action === 'cancel' && this.canCancel());
    if (!allowed) { this.conflict.set(true); return; }
    if (action === 'plan' || action === 'schedule') this.editPlan();
    const id = this.id;
    afterNextRender(() => {
      if (this.id !== id || this.item()?.version !== item.version) return;
      if (action === 'cancel') this.openCancel();
      else this.document.getElementById(action === 'plan' || action === 'schedule' ? 'schedule-start' : 'next-heading')?.focus();
    }, { injector: this.injector });
  }
  private resetForms(item: AdminAppointment) {
    this.fillPlan(item); this.transitionForm.reset(); this.cancelForm.reset(); this.planError.set(''); this.cancelError.set('');
    const reason: ContactReason = item.status === 'Scheduled' || item.status === 'InProgress' ? 'Agreement' : item.status === 'AwaitingPayment' ? 'ReadyForPayment' : 'NewAppointment';
    this.contactForm.setValue({ reason });
  }
  private fillPlan(item: AdminAppointment) {
    const start = item.repairPlan ? kyivInput(item.repairPlan.plannedStartUtc) : { local: '', offset: '' };
    const finish = item.repairPlan ? kyivInput(item.repairPlan.estimatedFinishUtc) : { local: '', offset: '' };
    this.planForm.reset({ price: item.repairPlan?.agreedPriceExact ?? '', start: start.local, startOffset: start.offset, finish: finish.local, finishOffset: finish.offset, inspectionPerformed: false });
  }
}
