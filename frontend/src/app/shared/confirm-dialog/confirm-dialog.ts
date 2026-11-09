import { Component, ElementRef, input, output, viewChild } from '@angular/core';

@Component({
  selector: 'app-confirm-dialog',
  template: `
    <dialog #dialog class="confirm-dialog" [attr.aria-labelledby]="dialogId() + '-title'"
      [attr.aria-describedby]="dialogId() + '-description'" (close)="closed.emit()">
      <h2 [id]="dialogId() + '-title'">{{ title() }}</h2>
      <p [id]="dialogId() + '-description'">{{ description() }}</p>
      <ng-content />
      <div class="dialog-actions">
        <button class="button button--secondary" type="button" (click)="close()">Скасувати</button>
        <button class="button button--danger" type="button" (click)="confirm()">{{ confirmLabel() }}</button>
      </div>
    </dialog>
  `,
})
export class ConfirmDialog {
  readonly dialogId = input.required<string>();
  readonly title = input.required<string>();
  readonly description = input.required<string>();
  readonly confirmLabel = input('Підтвердити');
  readonly confirmed = output<void>();
  readonly closed = output<void>();
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  open(): void { this.dialog().nativeElement.showModal(); }
  close(): void { this.dialog().nativeElement.close(); }
  confirm(): void { this.close(); this.confirmed.emit(); }
}
