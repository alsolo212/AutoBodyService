import { Component, ElementRef, signal, viewChild } from '@angular/core';

@Component({ selector: 'app-mobile-navigation', templateUrl: './mobile-navigation.html', styleUrl: './mobile-navigation.scss' })
export class MobileNavigation {
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('menu');
  readonly open = signal(false);
  show(): void { this.dialog().nativeElement.showModal(); this.open.set(true); }
  close(): void { this.dialog().nativeElement.close(); }
  followLink(event: MouseEvent): void {
    if (event.target instanceof Element && event.target.closest('a')) this.close();
  }
}
