import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';
export const phoneValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = String(control.value ?? '');
  if (value.length > 32 || /[^0-9+ ()-]/.test(value)) return { phone: true };
  const compact = value.replace(/[ ()-]/g, '');
  return /^(?:\+380[0-9]{9}|380[0-9]{9}|0[0-9]{9})$/.test(compact) ? null : { phone: true };
};
export const nameValidator: ValidatorFn = control => String(control.value ?? '').trim() ? null : { required: true };
export const passwordValidator: ValidatorFn = control => {
  const value = String(control.value ?? '');
  return value.length >= 12 && value.length <= 256 && /[a-z]/.test(value) && /[A-Z]/.test(value) && /[0-9]/.test(value) && /[^a-zA-Z0-9]/.test(value) ? null : { passwordPolicy: true };
};
export const passwordsMatch: ValidatorFn = group => group.get('password')?.value === group.get('confirmPassword')?.value ? null : { mismatch: true };
