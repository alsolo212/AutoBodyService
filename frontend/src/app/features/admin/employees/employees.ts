import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { Employee, EmployeesApi } from './employees-api';

const validText: ValidatorFn = control => {
  const text = String(control.value ?? '');
  return text.trim() && !/\p{Cc}/u.test(text) ? null : { invalidText: true };
};

@Component({
  selector: 'app-employees',
  imports: [ReactiveFormsModule, RouterLink, ConfirmDialog],
  templateUrl: './employees.html',
  styleUrl: './employees.scss',
})
export class Employees {
  private readonly api = inject(EmployeesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly dialog = viewChild.required(ConfirmDialog);
  private readonly nameInput = viewChild.required<ElementRef<HTMLInputElement>>('nameInput');
  readonly employees = signal<Employee[]>([]);
  readonly loading = signal(false);
  readonly loadError = signal('');
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly saving = signal(false);
  readonly editing = signal<Employee | null>(null);
  readonly deactivating = signal<Employee | null>(null);
  readonly form = this.fb.group({
    fullName: ['', [validText, Validators.maxLength(200)]],
    specialization: ['', [validText, Validators.maxLength(200)]],
  });

  constructor() { this.load(); }
  load() {
    if (this.saving() || this.loading()) return;
    this.loading.set(true); this.loadError.set('');
    this.api.list().pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: employees => this.employees.set(employees),
      error: (error: unknown) => this.loadError.set(this.message(error, 'Не вдалося завантажити працівників.')),
    });
  }
  edit(employee: Employee) {
    if (this.saving()) return;
    this.editing.set(employee); this.actionError.set(''); this.notice.set('');
    this.form.reset({ fullName: employee.fullName, specialization: employee.specialization });
    this.nameInput().nativeElement.focus();
  }
  cancelEdit() {
    this.editing.set(null); this.form.reset({ fullName: '', specialization: '' }); this.actionError.set('');
  }
  save() {
    if (this.saving() || this.loading() || this.loadError()) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const value = this.form.getRawValue();
    const body = { fullName: value.fullName.trim(), specialization: value.specialization.trim() };
    const current = this.editing();
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    (current ? this.api.update(current.id, current.version, body) : this.api.create(body)).pipe(
      finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: employee => {
        this.employees.update(items => [...items.filter(item => item.id !== employee.id), employee].sort((a, b) => a.fullName.localeCompare(b.fullName, 'uk')));
        this.cancelEdit(); this.notice.set(current ? 'Дані працівника збережено.' : 'Працівника додано.');
      },
      error: (error: unknown) => this.actionError.set(this.message(error, 'Не вдалося зберегти працівника.')),
    });
  }
  confirmDeactivation(employee: Employee) {
    if (this.saving()) return;
    this.deactivating.set(employee); this.actionError.set(''); this.notice.set(''); this.dialog().open();
  }
  deactivate() {
    const employee = this.deactivating();
    if (employee) this.setActive(employee, false);
  }
  setActive(employee: Employee, active: boolean) {
    if (this.saving()) return;
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    this.api.update(employee.id, employee.version, { isActive: active }).pipe(
      finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: updated => {
        this.employees.update(items => items.map(item => item.id === updated.id ? updated : item));
        if (this.editing()?.id === updated.id) this.editing.set(updated);
        this.notice.set(active ? 'Працівника активовано.' : 'Працівника деактивовано.');
      },
      error: (error: unknown) => this.actionError.set(this.message(error, 'Не вдалося змінити активність.')),
    });
  }
  private message(error: unknown, fallback: string) {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
      if (error.status === 403) return 'Доступ до працівників заборонено.';
      if (error.status === 409) return 'Дані працівника змінилися. Оновіть список і повторіть дію.';
      if (error.status === 404) return 'Працівника не знайдено. Оновіть список.';
      if (error.status === 400) return 'Сервер відхилив дані. Перевірте введені значення.';
    }
    return `${fallback} Перевірте з’єднання та спробуйте ще раз.`;
  }
}
