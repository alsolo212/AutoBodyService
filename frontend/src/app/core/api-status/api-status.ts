import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Subject, catchError, map, of, startWith, switchMap, timeout } from 'rxjs';
import { ApiHealth } from '../api-health';
import { UiState } from '../../shared/ui-state/ui-state';

@Component({
  selector: 'app-api-status',
  imports: [UiState],
  template: `
    <section class="api-connectivity" aria-label="Стан підключення до сервісу">
      @switch (state()) {
        @case ('loading') {
          <app-ui-state kind="loading" title="Перевіряємо з’єднання" />
        }
        @case ('healthy') {
          <p class="api-connectivity__success" role="status">З’єднання із сервісом встановлено.</p>
        }
        @case ('error') {
          <app-ui-state kind="error" title="Не вдалося підключитися до сервісу" description="Перевірте з’єднання та спробуйте ще раз.">
            <button class="button button--secondary api-connectivity__retry" type="button" (click)="retry()">Спробувати ще раз</button>
          </app-ui-state>
        }
      }
    </section>
  `,
})
export class ApiStatus {
  private readonly apiHealth = inject(ApiHealth);
  private readonly refresh = new Subject<void>();
  protected readonly state = toSignal(
    this.refresh.pipe(
      startWith(undefined),
      switchMap(() => this.apiHealth.getStatus().pipe(
        timeout(8000),
        map(response => response.status === 'Healthy' ? 'healthy' as const : 'error' as const),
        catchError(() => of('error' as const)),
        startWith('loading' as const),
      )),
    ),
    { initialValue: 'loading' as const },
  );

  protected retry(): void { this.refresh.next(); }
}
