import { DOCUMENT } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { Router } from '@angular/router';

@Component({ selector: 'app-skip-link', templateUrl: './skip-link.html' })
export class SkipLink {
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);
  readonly targetId = input.required<string>();
  get href(): string { return `${this.router.url.split('#')[0]}#${this.targetId()}`; }
  skip(event: MouseEvent): void {
    const target = this.document.getElementById(this.targetId());
    if (!target) return;
    event.preventDefault();
    target.focus();
    target.scrollIntoView({ block: 'start' });
  }
}
