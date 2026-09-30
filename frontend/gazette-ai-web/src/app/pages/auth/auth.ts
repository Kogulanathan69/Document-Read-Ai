import {
  ChangeDetectorRef,
  Component,
  OnDestroy,
  inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  FormBuilder,
  ReactiveFormsModule,
  Validators
} from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../services/auth';

type AuthView = 'login' | 'register' | 'verify';

@Component({
  selector: 'app-auth',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './auth.html',
  styleUrl: './auth.scss'
})
export class Auth implements OnDestroy {
  private readonly formBuilder = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly changeDetector = inject(ChangeDetectorRef);

  view: AuthView = 'login';
  isSubmitting = false;
  showPassword = false;
  errorMessage = '';
  successMessage = '';
  pendingEmail = '';
  resendSeconds = 0;

  private resendTimer: ReturnType<typeof setInterval> | null = null;

  readonly loginForm = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]]
  });

  readonly registerForm = this.formBuilder.nonNullable.group({
    fullName: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8), Validators.maxLength(100)]],
    confirmPassword: ['', Validators.required]
  });

  readonly verifyForm = this.formBuilder.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]]
  });

  constructor() {
    if (this.authService.isAuthenticated()) {
      void this.navigateAfterAuthentication();
    }
  }

  switchView(view: 'login' | 'register'): void {
    this.view = view;
    this.errorMessage = '';
    this.successMessage = '';
    this.showPassword = false;
  }

  submitLogin(): void {
    this.errorMessage = '';
    this.successMessage = '';

    if (this.loginForm.invalid) {
      this.loginForm.markAllAsTouched();
      return;
    }

    this.isSubmitting = true;

    this.authService
      .login(this.loginForm.getRawValue())
      .pipe(finalize(() => {
        this.isSubmitting = false;
        this.changeDetector.markForCheck();
      }))
      .subscribe({
        next: () => void this.navigateAfterAuthentication(),
        error: error => {
          this.errorMessage = this.getErrorMessage(
            error,
            'Login failed. Please check your email and password.'
          );
        }
      });
  }

  submitRegister(): void {
    this.errorMessage = '';
    this.successMessage = '';

    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      return;
    }

    const form = this.registerForm.getRawValue();

    if (form.password !== form.confirmPassword) {
      this.errorMessage = 'Passwords do not match.';
      return;
    }

    this.isSubmitting = true;

    this.authService
      .register({
        fullName: form.fullName.trim(),
        email: form.email.trim(),
        password: form.password
      })
      .pipe(finalize(() => {
        this.isSubmitting = false;
        this.changeDetector.markForCheck();
      }))
      .subscribe({
        next: response => {
          this.pendingEmail =
            response.email?.trim() || form.email.trim();
          this.view = 'verify';
          this.verifyForm.reset();
          this.successMessage = response.message ||
            'We sent a 6-digit verification code to your email.';
          this.startResendCountdown(60);
        },
        error: error => {
          this.errorMessage = this.getErrorMessage(
            error,
            'Registration failed. Please try again.'
          );
        }
      });
  }

  onOtpInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const code = input.value.replace(/\D/g, '').slice(0, 6);

    this.verifyForm.controls.code.setValue(
      code,
      { emitEvent: false }
    );

    input.value = code;
    this.errorMessage = '';

    if (code.length === 6 && !this.isSubmitting) {
      this.submitVerification();
    }
  }

  submitVerification(): void {
    this.errorMessage = '';

    if (!this.pendingEmail) {
      this.errorMessage = 'Registration email is missing. Please register again.';
      this.view = 'register';
      return;
    }

    if (this.verifyForm.invalid) {
      this.verifyForm.markAllAsTouched();
      return;
    }

    this.isSubmitting = true;

    this.authService
      .verifyEmail({
        email: this.pendingEmail,
        code: this.verifyForm.controls.code.value
      })
      .pipe(finalize(() => {
        this.isSubmitting = false;
        this.changeDetector.markForCheck();
      }))
      .subscribe({
        next: () => void this.navigateAfterAuthentication(),
        error: error => {
          this.verifyForm.controls.code.setValue('');
          this.errorMessage = this.getErrorMessage(
            error,
            'Invalid or expired verification code.'
          );
        }
      });
  }

  resendOtp(): void {
    if (!this.pendingEmail || this.resendSeconds > 0 || this.isSubmitting) {
      return;
    }

    this.errorMessage = '';
    this.successMessage = '';
    this.isSubmitting = true;

    this.authService
      .resendOtp(this.pendingEmail)
      .pipe(finalize(() => {
        this.isSubmitting = false;
        this.changeDetector.markForCheck();
      }))
      .subscribe({
        next: response => {
          this.successMessage = response.message ||
            'A new verification code has been sent.';
          this.verifyForm.reset();
          this.startResendCountdown(60);
        },
        error: error => {
          this.errorMessage = this.getErrorMessage(
            error,
            'Could not resend the verification code.'
          );
        }
      });
  }

  editEmail(): void {
    this.stopResendCountdown();
    this.registerForm.controls.email.setValue(this.pendingEmail);
    this.view = 'register';
    this.errorMessage = '';
    this.successMessage = '';
  }

  ngOnDestroy(): void {
    this.stopResendCountdown();
  }

  private async navigateAfterAuthentication(): Promise<void> {
    const returnUrl =
      this.route.snapshot.queryParamMap.get('returnUrl');

    await this.router.navigateByUrl(
      returnUrl?.startsWith('/') ? returnUrl : '/documents'
    );
  }

  private startResendCountdown(seconds: number): void {
    this.stopResendCountdown();
    this.resendSeconds = seconds;

    this.resendTimer = setInterval(() => {
      this.resendSeconds = Math.max(0, this.resendSeconds - 1);

      if (this.resendSeconds === 0) {
        this.stopResendCountdown();
      }

      this.changeDetector.markForCheck();
    }, 1000);
  }

  private stopResendCountdown(): void {
    if (this.resendTimer) {
      clearInterval(this.resendTimer);
      this.resendTimer = null;
    }
  }

  private getErrorMessage(error: any, fallback: string): string {
    if (typeof error?.error?.message === 'string') {
      return error.error.message;
    }

    if (typeof error?.error === 'string') {
      return error.error;
    }

    if (error?.status === 0) {
      return 'Cannot connect to GazetteAI API. Please make sure the backend is running.';
    }

    return fallback;
  }
}
