import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { ConfirmDialog } from './confirm-dialog';

describe('ConfirmDialog', () => {
  it('only emits confirmation after the confirm action', async () => {
    await TestBed.configureTestingModule({ imports: [ConfirmDialog] }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialog);
    fixture.componentRef.setInput('dialogId', 'confirm-test');
    fixture.componentRef.setInput('title', 'Підтвердьте дію');
    fixture.componentRef.setInput('description', 'Перевірте деталі.');
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    dialog.showModal = vi.fn();
    dialog.close = vi.fn();
    const confirmed = vi.fn();
    fixture.componentInstance.confirmed.subscribe(confirmed);

    fixture.componentInstance.open();
    expect(dialog.showModal).toHaveBeenCalledOnce();
    (fixture.nativeElement.querySelector('.button--secondary') as HTMLButtonElement).click();
    expect(confirmed).not.toHaveBeenCalled();
    (fixture.nativeElement.querySelector('.button--danger') as HTMLButtonElement).click();
    expect(dialog.close).toHaveBeenCalledTimes(2);
    expect(confirmed).toHaveBeenCalledOnce();
  });
});
