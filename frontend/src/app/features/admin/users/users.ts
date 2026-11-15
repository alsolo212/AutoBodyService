import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Observable, Subscription, finalize } from 'rxjs';
import { AuthService, AuthSession } from '../../../core/auth';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { ManagedUser, TemporaryPassword, UsersApi } from './users-api';

type Action = 'reset' | 'role' | 'block';
type PendingAction = { action: Action; user: ManagedUser };
function accountKey(session: AuthSession | null): string | null {
  return session ? `${session.phone}|${[...session.roles].sort().join(',')}|${session.mustChangePassword}` : null;
}

@Component({
  selector: 'app-users',
  imports: [ReactiveFormsModule, ConfirmDialog],
  templateUrl: './users.html',
  styleUrl: './users.scss',
})
export class Users {
  private readonly api = inject(UsersApi);
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly confirmDialog = viewChild.required(ConfirmDialog);
  private readonly secretDialog = viewChild<ElementRef<HTMLDialogElement>>('secretDialog');
  private searchSubscription?: Subscription;
  private actorKey = accountKey(this.auth.session());
  readonly searchForm = this.fb.group({ search: ['', Validators.maxLength(100)] });
  readonly users = signal<ManagedUser[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly loadError = signal('');
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly pending = signal<PendingAction | null>(null);
  readonly secret = signal<string | null>(null);
  readonly isSuperAdmin = computed(() => this.auth.session()?.roles.includes('SuperAdmin') ?? false);
  readonly confirmTitle = computed(() => {
    const pending = this.pending();
    if (!pending) return '';
    if (pending.action === 'reset') return 'Скинути пароль?';
    if (pending.action === 'role') return pending.user.isAdmin ? 'Відкликати роль адміністратора?' : 'Призначити адміністратором?';
    return pending.user.isBlocked ? 'Розблокувати обліковий запис?' : 'Заблокувати обліковий запис?';
  });
  readonly confirmDescription = computed(() => {
    const pending = this.pending();
    if (!pending) return '';
    const name = pending.user.name;
    if (pending.action === 'reset') return `Для ${name} буде створено тимчасовий пароль, а попередні сеанси буде завершено. Передайте пароль особисто поза системою.`;
    if (pending.action === 'role') return `Змінити роль для ${name}? Попередні сеанси користувача буде завершено.`;
    return pending.user.isBlocked
      ? `Відновити доступ для ${name}? Попередні сеанси залишаться недійсними.`
      : `Заблокувати ${name}? Доступ до захищених функцій і поточні сеанси буде припинено.`;
  });
  constructor() {
    effect(() => {
      const session = this.auth.session();
      const key = accountKey(session);
      if (key === this.actorKey) return;
      this.actorKey = key;
      this.searchSubscription?.unsubscribe();
      this.users.set([]); this.pending.set(null); this.clearSecret();
      if (!session) { void this.router.navigateByUrl('/login'); return; }
      if (session.mustChangePassword) { void this.router.navigateByUrl('/account/change-temporary-password'); return; }
      if (!session.roles.some(role => role === 'Admin' || role === 'SuperAdmin')) { void this.router.navigateByUrl('/account/appointments'); return; }
      this.load();
    });
    const clearOnPageHide = () => this.clearSecret();
    window.addEventListener('pagehide', clearOnPageHide);
    this.destroyRef.onDestroy(() => { window.removeEventListener('pagehide', clearOnPageHide); this.secret.set(null); this.searchSubscription?.unsubscribe(); });
    this.load();
  }
  load() {
    if (this.searchForm.invalid) { this.searchForm.markAllAsTouched(); return; }
    this.searchSubscription?.unsubscribe();
    this.loading.set(true); this.loadError.set('');
    const key = this.actorKey;
    this.searchSubscription = this.api.search(this.searchForm.controls.search.value.trim()).pipe(
      finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: users => { if (key === accountKey(this.auth.session())) this.users.set(users.filter(user => this.isSuperAdmin() || !user.isAdmin)); },
      error: (error: unknown) => {
        if (key !== accountKey(this.auth.session())) return;
        this.users.set([]);
        this.loadError.set(this.message(error, 'Не вдалося завантажити користувачів.'));
        this.checkAuthorization(error);
      },
    });
  }
  ask(action: Action, user: ManagedUser) {
    if (this.actorKey === null || this.actorKey !== accountKey(this.auth.session()) || this.saving() || this.loading() || this.secret() || (action !== 'reset' && !this.isSuperAdmin())) return;
    this.pending.set({ action, user }); this.notice.set(''); this.actionError.set('');
    this.confirmDialog().open();
  }
  execute() {
    const pending = this.pending();
    if (!pending || this.actorKey === null || this.actorKey !== accountKey(this.auth.session()) || this.saving() || (pending.action !== 'reset' && !this.isSuperAdmin())) return;
    this.pending.set(null); this.saving.set(true); this.actionError.set(''); this.notice.set('');
    const key = this.actorKey;
    const action: Observable<TemporaryPassword | void> = pending.action === 'reset' ? this.api.reset(pending.user) : pending.action === 'role' ? this.api.role(pending.user) : this.api.block(pending.user);
    action.pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        if (key === null || key !== accountKey(this.auth.session())) return;
        if (pending.action === 'reset' && result && 'temporaryPassword' in result) {
          this.secret.set(result.temporaryPassword);
          this.secretDialog()?.nativeElement.showModal();
        } else this.notice.set('Зміни збережено.');
        this.load();
      },
      error: (error: unknown) => {
        if (key !== accountKey(this.auth.session())) return;
        this.actionError.set(this.message(error, 'Не вдалося виконати дію. Її результат може бути невідомим; оновіть список.'));
        this.checkAuthorization(error);
        if (error instanceof HttpErrorResponse && (error.status === 404 || error.status === 409)) this.load();
      },
    });
  }
  clearSecret() {
    this.secret.set(null);
    const dialog = this.secretDialog()?.nativeElement;
    // Clear the rendered value synchronously before a page can enter the back/forward cache.
    const rendered = dialog?.querySelector('.temporary-password');
    if (rendered) rendered.textContent = '';
    if (dialog?.open) dialog.close();
  }
  private checkAuthorization(error: unknown) {
    if (!(error instanceof HttpErrorResponse)) return;
    if (error.status === 401) { this.auth.invalidateSession(); return; }
    if (error.status === 403) this.auth.getSession().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ error: () => {} });
  }
  private message(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
      if (error.status === 403) return 'Недостатньо прав для цієї дії.';
      if (error.status === 404) return 'Користувача не знайдено або він більше недоступний. Список оновлено.';
      if (error.status === 409) return 'Дані користувача змінилися. Перевірте оновлений список і повторіть дію.';
      if (error.status === 429) return 'Забагато спроб. Зачекайте та спробуйте ще раз.';
    }
    return fallback;
  }
}
