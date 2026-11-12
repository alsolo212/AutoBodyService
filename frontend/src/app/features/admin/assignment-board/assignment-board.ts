import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { EMPTY, expand, finalize, forkJoin, reduce } from 'rxjs';
import { AdminAppointmentApi, AdminBoardCard, AdminBoardFilter } from '../appointment-details/admin-appointment-api';
import { ActiveAssignment, Employee, EmployeesApi } from '../employees/employees-api';

const filter: AdminBoardFilter = { search: '', from: '', to: '', sort: 'inspection', direction: 'asc', pageSize: 100 };

@Component({ selector: 'app-assignment-board', imports: [RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, CdkDragHandle],
  templateUrl: './assignment-board.html', styleUrl: './assignment-board.scss' })
export class AssignmentBoard {
  private readonly ordersApi = inject(AdminAppointmentApi);
  private readonly employeesApi = inject(EmployeesApi);
  private readonly destroyRef = inject(DestroyRef);
  readonly orders = signal<AdminBoardCard[]>([]);
  readonly employees = signal<Employee[]>([]);
  readonly assignments = signal<ActiveAssignment[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly selections = signal<Record<string, string>>({});

  constructor() { this.load(); }
  load() {
    if (this.loading()) return;
    this.loading.set(true); this.error.set('');
    const orders = this.ordersApi.list('InProgress', 1, filter).pipe(
      expand(page => page.page * page.pageSize < page.total ? this.ordersApi.list('InProgress', page.page + 1, filter) : EMPTY),
      reduce((all, page) => [...all, ...page.items], [] as AdminBoardCard[]),
    );
    forkJoin({ orders, employees: this.employeesApi.list(), assignments: this.employeesApi.activeAssignments() }).pipe(
      finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: data => {
        this.orders.set([...new Map(data.orders.filter(order => order.status === 'InProgress').map(order => [order.id, order])).values()]);
        this.employees.set(data.employees.filter(employee => employee.isActive));
        this.assignments.set(data.assignments); this.selections.set({});
      },
      error: (error: unknown) => { this.orders.set([]); this.employees.set([]); this.assignments.set([]); this.error.set(this.message(error, 'Не вдалося завантажити розподіл робіт.')); },
    });
  }
  assignmentsFor(employeeId: string) { return this.assignments().filter(a => a.employeeId === employeeId); }
  order(id: string) { return this.orders().find(order => order.id === id); }
  available(employeeId: string) { return this.orders().filter(order => this.canAssign(order, employeeId)); }
  select(employeeId: string, orderId: string) { this.selections.update(values => ({ ...values, [employeeId]: orderId })); }
  canAssign(order: AdminBoardCard, employeeId: string) {
    return order.status === 'InProgress' && this.orders().some(item => item.id === order.id) &&
      this.employees().some(employee => employee.id === employeeId && employee.isActive) &&
      !this.assignments().some(assignment => assignment.appointmentId === order.id && assignment.employeeId === employeeId);
  }
  readonly canDrop = (drag: CdkDrag<AdminBoardCard>, drop: CdkDropList<Employee>) =>
    !this.loading() && !this.saving() && drag.dropContainer.id === 'assignment-source' && this.canAssign(drag.data, drop.data.id);
  dropped(event: CdkDragDrop<Employee, AdminBoardCard[], AdminBoardCard>) {
    if (event.previousContainer.id !== 'assignment-source' || !event.isPointerOverContainer || this.loading() || this.saving() || !this.canAssign(event.item.data, event.container.data.id)) return;
    this.assign(event.item.data, event.container.data);
  }
  assignSelected(employee: Employee) {
    const order = this.order(this.selections()[employee.id] ?? '');
    if (order) this.assign(order, employee);
  }
  assign(order: AdminBoardCard, employee: Employee) {
    if (this.loading() || this.saving() || !this.canAssign(order, employee.id)) return;
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    this.employeesApi.assign(order.id, employee.id).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.notice.set('Працівника призначено.'); this.load(); },
      error: (error: unknown) => { this.actionError.set(this.message(error, 'Не вдалося призначити працівника.')); this.load(); },
    });
  }
  release(assignment: ActiveAssignment) {
    if (this.loading() || this.saving() || !this.assignments().some(item => item.id === assignment.id)) return;
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    this.employeesApi.release(assignment.id).pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.notice.set('Призначення завершено.'); this.load(); },
      error: (error: unknown) => { this.actionError.set(this.message(error, 'Не вдалося завершити призначення.')); this.load(); },
    });
  }
  private message(error: unknown, fallback: string) {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
      if (error.status === 403) return 'Доступ до розподілу робіт заборонено.';
      if (error.status === 409) return 'Дані змінилися або призначення вже існує. Перевірте актуальний список.';
      if (error.status === 404) return 'Замовлення або працівника не знайдено. Перевірте актуальний список.';
    }
    return `${fallback} Перевірте з’єднання та спробуйте ще раз.`;
  }
}
