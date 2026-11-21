import { AdminShell } from '../../../layout/admin-shell/admin-shell';
import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { DOCUMENT } from '@angular/common';
import { Component, DestroyRef, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, NavigationStart, Router, RouterLink } from '@angular/router';
import { Subject, catchError, filter, finalize, forkJoin, map, of, switchMap } from 'rxjs';
import { AppointmentStatus, price, statusLabel } from '../../appointments/appointments-api';
import { AdminAppointmentApi, AdminBoardCard, AdminBoardFilter, AdminBoardPage } from '../appointment-details/admin-appointment-api';
import { kyivWithOffset } from '../appointment-details/kyiv-date';
import { kyivDate } from '../../../shared/time/kyiv-time';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { BoardSnapshot, BoardState, StatusFilter } from './board-state';

interface Column { status: AppointmentStatus; page: number; total: number; items: AdminBoardCard[]; filtered: boolean; }
const statuses: AppointmentStatus[] = ['AwaitingInspection', 'Scheduled', 'InProgress', 'Paused', 'AwaitingPayment', 'Completed', 'Cancelled'];
const successor: Partial<Record<AppointmentStatus, AppointmentStatus>> = {
  AwaitingInspection: 'Scheduled', Scheduled: 'InProgress', InProgress: 'AwaitingPayment', Paused: 'InProgress', AwaitingPayment: 'Completed',
};
const emptyColumns = (): Column[] => statuses.map(status => ({ status, page: 1, total: 0, items: [], filtered: false }));

@Component({ selector: 'app-appointments-board', imports: [AdminShell, RouterLink, ReactiveFormsModule, CdkDropListGroup, CdkDropList, CdkDrag, CdkDragHandle, ConfirmDialog],
  templateUrl: './appointments-board.html', styleUrl: './appointments-board.scss' })
export class AppointmentsBoard {
  private readonly api = inject(AdminAppointmentApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly boardState = inject(BoardState);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly transitionDialog = viewChild<ConfirmDialog>('transitionDialog');
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly reloads = new Subject<void>();
  private filter: AdminBoardFilter = { search: '', from: '', to: '', sort: 'inspection', direction: 'asc', pageSize: 5 };
  private selectedStatus: StatusFilter = '';
  private pendingScroll: Pick<BoardSnapshot, 'scrollY' | 'scrollLeft'> | null = null;
  private pendingStatus: AppointmentStatus | null = null;
  readonly columns = signal<Column[]>(emptyColumns());
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly filtersExpanded = signal(false);
  readonly error = signal('');
  readonly actionMessage = signal('');
  readonly success = signal('');
  readonly pendingTransition = signal<{ card: AdminBoardCard; target: AppointmentStatus } | null>(null);
  readonly statusLabel = statusLabel;
  readonly price = price;
  readonly format = kyivWithOffset;
  readonly kyivDate = kyivDate;
  readonly statuses = statuses;
  readonly filterForm = this.fb.group({ search: ['', Validators.maxLength(100)], status: this.fb.control<StatusFilter>(''),
    from: [''], to: [''], sort: ['inspection'], direction: ['asc'], pageSize: [5] });

  constructor() {
    const previous = this.router.lastSuccessfulNavigation()?.finalUrl?.toString().split('?')[0].split('#')[0] ?? '';
    const restored = this.boardState.take(previous);
    if (restored) {
      this.filter = restored.filter; this.selectedStatus = restored.status;
      this.filterForm.setValue(restored.draft);
      this.columns.update(columns => columns.map(column => ({ ...column, page: restored.pages[column.status] ?? 1 })));
      this.filtersExpanded.set(restored.filtersExpanded);
      this.pendingScroll = { scrollY: restored.scrollY, scrollLeft: restored.scrollLeft };
    } else {
      const requestedStatus = this.route.snapshot.queryParamMap.get('status');
      if (requestedStatus && statuses.includes(requestedStatus as AppointmentStatus)) {
        this.selectedStatus = requestedStatus as AppointmentStatus;
        this.filterForm.controls.status.setValue(this.selectedStatus);
        this.pendingStatus = this.selectedStatus;
      }
    }
    this.router.events.pipe(filter((event): event is NavigationStart => event instanceof NavigationStart), takeUntilDestroyed(this.destroyRef)).subscribe(event => {
      const path = event.url.split('?')[0].split('#')[0];
      if (/^\/admin\/appointments\/(?!new$)[^/]+$/.test(path)) this.rememberBoard(path);
    });
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
      const outOfRange = this.columns().some(column => !column.filtered && column.page > this.maxPage(column));
      if (outOfRange) {
        this.columns.update(columns => columns.map(column => ({ ...column, page: Math.min(column.page, this.maxPage(column)) })));
        this.reload(); return;
      }
      this.restoreScroll();
      this.showRequestedStatus();
    });
    this.reload();
  }
  next(status: AppointmentStatus): AppointmentStatus | null { return successor[status] ?? null; }
  canCancel(status: AppointmentStatus) { return status === 'AwaitingInspection' || status === 'Scheduled' || status === 'InProgress' || status === 'Paused'; }
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
      this.filtersExpanded.set(true); this.error.set('Перевірте пошук і діапазон дат.'); return;
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
  hasAdvancedFilters(): boolean { return !!this.selectedStatus || !!this.filter.from || !!this.filter.to || this.filter.sort !== 'inspection' || this.filter.direction !== 'asc' || this.filter.pageSize !== 5; }
  scrollToStatus(status: AppointmentStatus) {
    const heading = this.document.getElementById(`column-${status}`);
    heading?.focus({ preventScroll: true });
    heading?.scrollIntoView?.({ block: 'start', inline: 'nearest' });
  }
  advance(card: AdminBoardCard, target = this.next(card.status)) {
    if (!target || target !== this.next(card.status) || this.loading() || this.saving()) return;
    this.actionMessage.set(''); this.success.set('');
    if (target === 'Scheduled' || target === 'Completed' || card.status === 'Paused') {
      const action = card.status === 'Paused' ? 'resume' : target === 'Completed' ? 'complete' : card.repairPlan ? 'schedule' : 'plan';
      void this.router.navigate(['/admin/appointments', card.id], { queryParams: { action, version: card.version } }); return;
    }
    this.pendingTransition.set({ card, target });
    this.transitionDialog()?.open();
  }
  confirmAdvance() {
    const pending = this.pendingTransition(); this.pendingTransition.set(null);
    if (!pending || this.loading() || this.saving()) return;
    const { card, target } = pending;
    const current = this.columns().find(column => column.status === card.status)?.items.find(item => item.id === card.id);
    if (!current || current.version !== card.version || this.next(current.status) !== target) {
      this.actionMessage.set('Замовлення змінилося. Оновіть дошку та перевірте стан.'); this.reload(); return;
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
  private rememberBoard(detailsPath: string): void {
    const viewport = this.document.defaultView;
    this.boardState.save({ filter: { ...this.filter }, status: this.selectedStatus, draft: this.filterForm.getRawValue(),
      pages: Object.fromEntries(this.columns().map(column => [column.status, column.page])),
      scrollY: viewport?.scrollY ?? 0, scrollLeft: this.document.querySelector<HTMLElement>('.board-scroll')?.scrollLeft ?? 0,
      filtersExpanded: this.filtersExpanded() }, detailsPath);
  }
  private restoreScroll(): void {
    const position = this.pendingScroll;
    const viewport = this.document.defaultView;
    if (!position || !viewport) return;
    this.pendingScroll = null;
    viewport.requestAnimationFrame(() => viewport.requestAnimationFrame(() => {
      if (this.destroyRef.destroyed) return;
      const board = this.document.querySelector<HTMLElement>('.board-scroll');
      if (board) board.scrollLeft = position.scrollLeft;
      viewport.scrollTo({ top: position.scrollY, behavior: 'instant' });
    }));
  }
  private showRequestedStatus(): void {
    const status = this.pendingStatus; const viewport = this.document.defaultView;
    if (!status || !viewport) return;
    this.pendingStatus = null;
    viewport.requestAnimationFrame(() => viewport.requestAnimationFrame(() => {
      if (!this.destroyRef.destroyed) this.scrollToStatus(status);
    }));
  }
  private resetPages() { this.columns.update(columns => columns.map(column => ({ ...column, page: 1 }))); }
}
