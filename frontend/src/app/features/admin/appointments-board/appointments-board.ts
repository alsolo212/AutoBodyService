import { NotificationBell } from '../../../shared/notification-bell/notification-bell';
import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Subject, catchError, finalize, forkJoin, map, of, switchMap } from 'rxjs';
import { AppointmentStatus, price, statusLabel } from '../../appointments/appointments-api';
import { AdminAppointmentApi, AdminBoardCard, AdminBoardFilter, AdminBoardPage } from '../appointment-details/admin-appointment-api';
import { kyivWithOffset } from '../appointment-details/kyiv-date';

type StatusFilter = AppointmentStatus | '';
interface Column { status: AppointmentStatus; page: number; total: number; items: AdminBoardCard[]; filtered: boolean; }
const statuses: AppointmentStatus[] = ['AwaitingInspection', 'Scheduled', 'InProgress', 'AwaitingPayment', 'Completed', 'Cancelled'];
const successor: Partial<Record<AppointmentStatus, AppointmentStatus>> = {
  AwaitingInspection: 'Scheduled', Scheduled: 'InProgress', InProgress: 'AwaitingPayment', AwaitingPayment: 'Completed',
};
const emptyColumns = (): Column[] => statuses.map(status => ({ status, page: 1, total: 0, items: [], filtered: false }));

@Component({ selector: 'app-appointments-board', imports: [NotificationBell, RouterLink, ReactiveFormsModule, CdkDropListGroup, CdkDropList, CdkDrag, CdkDragHandle],
  templateUrl: './appointments-board.html', styleUrl: './appointments-board.scss' })
export class AppointmentsBoard {
  private readonly api = inject(AdminAppointmentApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly reloads = new Subject<void>();
  private filter: AdminBoardFilter = { search: '', from: '', to: '', sort: 'inspection', direction: 'asc', pageSize: 5 };
  private selectedStatus: StatusFilter = '';
  readonly columns = signal<Column[]>(emptyColumns());
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly actionMessage = signal('');
  readonly success = signal('');
  readonly statusLabel = statusLabel;
  readonly price = price;
  readonly format = kyivWithOffset;
  readonly statuses = statuses;
  readonly filterForm = this.fb.group({ search: ['', Validators.maxLength(100)], status: this.fb.control<StatusFilter>(''),
    from: [''], to: [''], sort: ['inspection'], direction: ['asc'], pageSize: [5] });

  constructor() {
    this.reloads.pipe(switchMap(() => {
      this.loading.set(true); this.error.set('');
      const columns = this.columns();
      return forkJoin(columns.map(column => this.selectedStatus && column.status !== this.selectedStatus
        ? of({ status: column.status, page: null })
        : this.api.list(column.status, column.page, this.filter).pipe(map(page => ({ status: column.status, page }))))).pipe(
        catchError((error: unknown) => {
          this.error.set(error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ до замовлень заборонено.' :
            error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' : 'Не вдалося завантажити замовлення. Спробуйте ще раз.');
          return of(null);
        }), finalize(() => this.loading.set(false)),
      );
    }), takeUntilDestroyed(this.destroyRef)).subscribe(result => {
      if (!result) { this.columns.set(emptyColumns()); return; }
      this.columns.update(columns => columns.map(column => {
        const page = result.find(x => x.status === column.status)!.page;
        return page ? { ...column, page: page.page, total: page.total, items: page.items, filtered: false }
          : { ...column, page: 1, total: 0, items: [], filtered: true };
      }));
    });
    this.reload();
  }
  next(status: AppointmentStatus): AppointmentStatus | null { return successor[status] ?? null; }
  canCancel(status: AppointmentStatus) { return status === 'AwaitingInspection' || status === 'Scheduled' || status === 'InProgress'; }
  readonly canEnter = (drag: CdkDrag<AdminBoardCard>, drop: CdkDropList<Column>) =>
    !this.loading() && !this.saving() && this.next(drag.data.status) === drop.data.status;
  dropped(event: CdkDragDrop<Column, Column, AdminBoardCard>) {
    if (!event.isPointerOverContainer || event.previousContainer === event.container || !this.canEnter(event.item, event.container)) return;
    this.advance(event.item.data, event.container.data.status);
  }
  applyFilters() {
    this.filterForm.markAllAsTouched();
    const value = this.filterForm.getRawValue();
    if (this.filterForm.invalid || (value.from && value.to && value.from > value.to)) {
      this.error.set('Перевірте пошук і діапазон дат.'); return;
    }
    this.filter = { search: value.search, from: value.from, to: value.to, sort: value.sort, direction: value.direction, pageSize: Number(value.pageSize) };
    this.selectedStatus = value.status; this.actionMessage.set(''); this.success.set(''); this.resetPages(); this.reload();
  }
  changePage(status: AppointmentStatus, delta: number) {
    if (this.loading() || this.saving()) return;
    const col = this.columns().find(c => c.status === status); if (!col || col.filtered) return;
    const page = col.page + delta, max = Math.max(1, Math.ceil(col.total / this.filter.pageSize));
    if (page < 1 || page > max) return;
    this.columns.update(columns => columns.map(c => c.status === status ? { ...c, page } : c));
    this.reload();
  }
  maxPage(column: Column) { return Math.max(1, Math.ceil(column.total / this.filter.pageSize)); }
  advance(card: AdminBoardCard, target = this.next(card.status)) {
    if (!target || target !== this.next(card.status) || this.loading() || this.saving()) return;
    this.actionMessage.set(''); this.success.set('');
    if (target === 'Scheduled' || target === 'Completed') {
      const action = target === 'Completed' ? 'complete' : card.repairPlan ? 'schedule' : 'plan';
      void this.router.navigate(['/admin/appointments', card.id], { queryParams: { action, version: card.version } }); return;
    }
    this.saving.set(true);
    this.api.transition(card.id, { version: card.version, target }).pipe(
      finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: () => { this.success.set('Статус оновлено.'); this.resetPages(); this.reload(); },
      error: (error: unknown) => {
        this.actionMessage.set(error instanceof HttpErrorResponse && error.status === 409
          ? 'Замовлення змінилося. Дошку оновлено; перевірте стан перед наступною дією.'
          : error instanceof HttpErrorResponse && error.status === 403 ? 'Доступ до дії заборонено.'
          : error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.'
          : error instanceof HttpErrorResponse && error.status === 404 ? 'Замовлення не знайдено. Дошку оновлено.'
          : 'Не вдалося змінити статус. Перевірте стан замовлення та спробуйте ще раз.');
        this.resetPages(); this.reload();
      },
    });
  }
  cancel(card: AdminBoardCard) {
    if (!this.canCancel(card.status) || this.loading() || this.saving()) return;
    void this.router.navigate(['/admin/appointments', card.id], { queryParams: { action: 'cancel', version: card.version } });
  }
  reload() { this.reloads.next(); }
  private resetPages() { this.columns.update(columns => columns.map(column => ({ ...column, page: 1 }))); }
}
