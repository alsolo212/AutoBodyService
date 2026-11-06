import { Component, input } from '@angular/core';

@Component({
  selector: 'app-ui-state',
  template: `
    <div class="ui-state" [class.ui-state--error]="kind() === 'error'"
      [attr.role]="kind() === 'error' ? 'alert' : 'status'" [attr.aria-busy]="kind() === 'loading'">
      @if (kind() === 'loading') { <span class="spinner" aria-hidden="true"></span> }
      <div>
        <strong>{{ title() }}</strong>
        @if (description()) { <p>{{ description() }}</p> }
        <ng-content />
      </div>
    </div>
  `,
})
export class UiState {
  readonly kind = input.required<'loading' | 'empty' | 'error'>();
  readonly title = input.required<string>();
  readonly description = input('');
}
