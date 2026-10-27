import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faArrowsRotate, faChevronDown, faMagnifyingGlassChart } from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { DashboardStore } from '../../core/dashboard-store';
import { LayoutService } from '../../core/layout';
import { ImpactLevel, InstrumentDto, NewsItem, Sentiment, Severity, Signal } from '../../core/models';
import { ToastService } from '../../core/ui-state';
import { IstPipe, PricePipe } from '../../shared/format';
import { BIAS_BADGE } from './trade-grid';

type Tab = 'signals' | 'news' | 'events';

const IMPACT_BADGE: Record<ImpactLevel, string> = { LOW: 'text-bg-secondary', MEDIUM: 'text-bg-info', HIGH: 'text-bg-warning', EXTREME_HALT: 'text-bg-danger' };
const SEVERITY_BADGE: Record<Severity, string> = { INFO: 'text-bg-secondary', WARNING: 'text-bg-warning', CRITICAL: 'text-bg-danger' };
const SIGNAL_BADGE: Record<string, string> = { NEW: 'text-bg-info', DISPATCHED: 'text-bg-success', REJECTED: 'text-bg-secondary', EXPIRED: 'text-bg-dark' };

@Component({
  selector: 'tb-info-tabs',
  imports: [FormsModule, FaIconComponent, IstPipe, PricePipe],
  template: `
    <section class="tb-panel" aria-label="Signals, news and events">
      <div class="d-flex align-items-center gap-2 p-2 border-bottom flex-wrap">
        <ul class="nav nav-pills nav-sm" [class.nav-fill]="layout.phone()" [class.w-100]="layout.phone()" role="tablist">
          @for (t of tabs; track t.id) {
            <li class="nav-item" role="presentation">
              <button type="button" class="nav-link py-1 px-3" role="tab" [class.active]="tab() === t.id" [attr.aria-selected]="tab() === t.id" (click)="selectTab(t.id)">
                {{ layout.phone() ? t.short : t.label }} <span class="badge text-bg-secondary ms-1">{{ count(t.id) }}</span>
              </button>
            </li>
          }
        </ul>
        <div class="ms-auto d-flex flex-wrap justify-content-end gap-2 align-items-center">
          @if (tab() === 'news') {
            <label class="visually-hidden" for="tb-scan-target">Scan target</label>
            <select id="tb-scan-target" class="form-select form-select-sm w-auto" [ngModel]="scanTarget()" (ngModelChange)="scanTarget.set($event)">
              <option value="">Whole market</option>
              @for (i of instruments(); track i.instrumentId) { <option [value]="'' + i.instrumentId">{{ i.symbol }}</option> }
            </select>
            <button type="button" class="btn btn-sm btn-outline-info" [disabled]="scanning()" (click)="scan()"><fa-icon [icon]="i.scan" class="me-1" />Scan now</button>
          }
          <button type="button" class="btn btn-sm btn-outline-secondary" title="Refresh" aria-label="Refresh" (click)="refresh()"><fa-icon [icon]="i.refresh" /></button>
        </div>
      </div>

      <!-- Phones: lists in the page flow; wider screens: scrollable tables -->
      <div [class.table-responsive]="!layout.phone()" [style.max-height]="layout.phone() ? null : '380px'">
        @switch (tab()) {
          @case ('signals') {
            @if (layout.phone()) {
              <ul class="tb-list" aria-label="Signals">
                @for (s of signals(); track s.signalId) {
                  <li class="tb-list-item" [class.tb-expanded]="expanded() === 's' + s.signalId">
                    <button type="button" class="tb-list-toggle d-flex align-items-center gap-2 w-100" [attr.aria-expanded]="expanded() === 's' + s.signalId" (click)="toggle('s' + s.signalId)">
                      <span class="flex-grow-1 min-w-0">
                        <span class="d-block fw-semibold text-truncate">{{ s.security?.pmlSymbol ?? s.securityId }}</span>
                        <span class="d-block small text-body-secondary text-truncate">{{ s.createdAt | ist }} · {{ s.detection?.pattern?.patternName ?? '—' }} {{ s.detection?.timeframe }}</span>
                      </span>
                      <span class="text-end">
                        <span class="badge" [class]="signalBadge(s.status)">{{ s.status }}</span>
                        <span class="d-block small num text-body-secondary">conf {{ s.combinedAiConfidence }}</span>
                      </span>
                      <fa-icon [icon]="i.chevron" class="tb-chevron text-body-secondary" />
                    </button>
                    @if (expanded() === 's' + s.signalId) {
                      <div class="row g-2 small mt-1">
                        <div class="col-4"><div class="tb-label">Entry</div><div class="num">{{ s.entryOptionLtp | price }}</div></div>
                        <div class="col-4"><div class="tb-label">SL</div><div class="num text-danger">{{ s.mandatorySlPrice | price }}</div></div>
                        <div class="col-4"><div class="tb-label">Target</div><div class="num text-success">{{ s.mandatoryTargetPrice | price }}</div></div>
                        <div class="col-4"><div class="tb-label">RR</div><div class="num">{{ s.riskRewardRatio }}</div></div>
                        <div class="col-8"><div class="tb-label">Pattern / News / OI</div><div class="num">{{ s.patternScore }} / {{ s.newsScore }} / {{ s.oiGreeksScore }}</div></div>
                      </div>
                      @if (s.rejectionReason) { <div class="small text-body-secondary mt-1">{{ s.rejectionReason }}</div> }
                    }
                  </li>
                } @empty { <li class="tb-list-item text-center text-body-secondary py-4">No signals yet today.</li> }
              </ul>
            } @else {
            <table class="table table-sm tb-grid mb-0">
              <thead><tr><th>Time</th><th>Contract</th><th>Pattern</th><th class="text-end">Entry</th><th class="text-end">SL</th><th class="text-end">Target</th><th class="text-end">RR</th><th class="text-end">Pat / News / OI</th><th class="text-end">Conf.</th><th>Status</th><th>Reason</th></tr></thead>
              <tbody>
                @for (s of signals(); track s.signalId) {
                  <tr>
                    <td class="num">{{ s.createdAt | ist }}</td><td class="text-nowrap">{{ s.security?.pmlSymbol ?? s.securityId }}</td>
                    <td class="text-nowrap">{{ s.detection?.pattern?.patternName ?? '—' }} <span class="text-body-secondary small">{{ s.detection?.timeframe }}</span></td>
                    <td class="num text-end">{{ s.entryOptionLtp | price }}</td><td class="num text-end text-danger">{{ s.mandatorySlPrice | price }}</td>
                    <td class="num text-end text-success">{{ s.mandatoryTargetPrice | price }}</td><td class="num text-end">{{ s.riskRewardRatio }}</td>
                    <td class="num text-end">{{ s.patternScore }} / {{ s.newsScore }} / {{ s.oiGreeksScore }}</td><td class="num text-end fw-semibold">{{ s.combinedAiConfidence }}</td>
                    <td><span class="badge" [class]="signalBadge(s.status)">{{ s.status }}</span></td>
                    <td class="small text-body-secondary text-truncate" style="max-width: 320px" [title]="s.rejectionReason ?? ''">{{ s.rejectionReason ?? '' }}</td>
                  </tr>
                } @empty { <tr><td colspan="11" class="text-center text-body-secondary py-4">No signals yet today.</td></tr> }
              </tbody>
            </table>
            }
          }
          @case ('news') {
            <div class="p-2 d-flex flex-wrap gap-2 border-bottom">
              @for (s of sentiment(); track s.sentimentId) {
                <span class="badge border" [class]="bias(s.dominantBias)" [title]="s.haltReason ?? ''">
                  {{ symbol(s.instrumentId) }} {{ s.netSentimentScore > 0 ? '+' : '' }}{{ s.netSentimentScore }}
                  @if (s.tradingHaltActive) { · HALT }
                </span>
              } @empty { <span class="small text-body-secondary">No sentiment computed yet (pre-market scan runs at 08:30 IST).</span> }
            </div>
            @if (layout.phone()) {
              <ul class="tb-list" aria-label="News">
                @for (n of news(); track n.newsId) {
                  <li class="tb-list-item" [class.tb-expanded]="expanded() === 'n' + n.newsId">
                    <button type="button" class="tb-list-toggle d-flex align-items-start gap-2 w-100" [attr.aria-expanded]="expanded() === 'n' + n.newsId" (click)="toggle('n' + n.newsId)">
                      <span class="flex-grow-1 min-w-0">
                        <span class="d-block" [class.tb-clamp-2]="expanded() !== 'n' + n.newsId">{{ n.headline }}</span>
                        <span class="d-block small text-body-secondary">{{ n.company?.nseSymbol ?? (n.instrumentId ? symbol(n.instrumentId) : 'Market') }} · {{ n.analyzedAt | ist }}</span>
                      </span>
                      <span class="text-end">
                        <span class="badge" [class]="impact(n.impactLevel)">{{ n.impactLevel }}</span>
                        <span class="d-block small num" [class]="n.sentimentScore > 0 ? 'text-success' : n.sentimentScore < 0 ? 'text-danger' : ''">{{ n.sentimentScore }}</span>
                      </span>
                      <fa-icon [icon]="i.chevron" class="tb-chevron text-body-secondary" />
                    </button>
                    @if (expanded() === 'n' + n.newsId) {
                      @if (n.summary) { <div class="small mt-1">{{ n.summary }}</div> }
                      <div class="small text-body-secondary mt-1">
                        {{ n.newsCategory }} · {{ n.scanType }}
                        @for (u of urls(n); track u; let idx = $index) { <a class="ms-1" [href]="u" target="_blank" rel="noopener noreferrer">Source {{ idx + 1 }}</a> }
                      </div>
                    }
                  </li>
                } @empty { <li class="tb-list-item text-center text-body-secondary py-4">No news stored today.</li> }
              </ul>
            } @else {
            <table class="table table-sm tb-grid mb-0">
              <thead><tr><th>Time</th><th>Target</th><th>Headline</th><th>Category</th><th>Impact</th><th class="text-end">Score</th><th>Scan</th></tr></thead>
              <tbody>
                @for (n of news(); track n.newsId) {
                  <tr>
                    <td class="num">{{ n.analyzedAt | ist }}</td>
                    <td class="text-nowrap">{{ n.company?.nseSymbol ?? (n.instrumentId ? symbol(n.instrumentId) : 'Market') }}</td>
                    <td>
                      {{ n.headline }}
                      @for (u of urls(n); track u; let idx = $index) { <a class="small ms-1" [href]="u" target="_blank" rel="noopener noreferrer">[{{ idx + 1 }}]</a> }
                    </td>
                    <td class="small">{{ n.newsCategory }}</td>
                    <td><span class="badge" [class]="impact(n.impactLevel)">{{ n.impactLevel }}</span></td>
                    <td class="num text-end" [class]="n.sentimentScore > 0 ? 'text-success' : n.sentimentScore < 0 ? 'text-danger' : ''">{{ n.sentimentScore }}</td>
                    <td class="small text-body-secondary">{{ n.scanType }}</td>
                  </tr>
                } @empty { <tr><td colspan="7" class="text-center text-body-secondary py-4">No news stored today.</td></tr> }
              </tbody>
            </table>
            }
          }
          @case ('events') {
            @if (layout.phone()) {
              <ul class="tb-list" aria-label="Events">
                @for (e of store.events(); track e.eventId) {
                  <li class="tb-list-item" [class.tb-expanded]="expanded() === 'e' + e.eventId">
                    <button type="button" class="tb-list-toggle d-block w-100" [attr.aria-expanded]="expanded() === 'e' + e.eventId" (click)="toggle('e' + e.eventId)">
                      <span class="d-flex align-items-center gap-2 small">
                        <span class="badge" [class]="severity(e.severity)">{{ e.severity }}</span>
                        <span class="fw-semibold text-truncate">{{ e.eventType }}</span>
                        <span class="ms-auto num text-body-secondary text-nowrap">{{ e.createdAt | ist }}</span>
                        <fa-icon [icon]="i.chevron" class="tb-chevron text-body-secondary" />
                      </span>
                      <span class="d-block small text-body-secondary" [class.text-truncate]="expanded() !== 'e' + e.eventId">{{ e.message }}</span>
                    </button>
                    @if (expanded() === 'e' + e.eventId) {
                      <div class="small text-body-secondary mt-1">{{ e.createdAt | ist: true }}@if (e.accountId !== null) { · {{ accountName(e.accountId) }} }</div>
                    }
                  </li>
                } @empty { <li class="tb-list-item text-center text-body-secondary py-4">No events.</li> }
              </ul>
            } @else {
            <table class="table table-sm tb-grid mb-0">
              <thead><tr><th>Time</th><th>Severity</th><th>Type</th><th>Account</th><th>Message</th></tr></thead>
              <tbody>
                @for (e of store.events(); track e.eventId) {
                  <tr>
                    <td class="num">{{ e.createdAt | ist: true }}</td><td><span class="badge" [class]="severity(e.severity)">{{ e.severity }}</span></td>
                    <td class="small text-nowrap">{{ e.eventType }}</td><td class="small">{{ accountName(e.accountId) }}</td><td class="small">{{ e.message }}</td>
                  </tr>
                } @empty { <tr><td colspan="5" class="text-center text-body-secondary py-4">No events.</td></tr> }
              </tbody>
            </table>
            }
          }
        }
      </div>
    </section>
  `,
})
export class InfoTabs {
  protected readonly store = inject(DashboardStore);
  protected readonly layout = inject(LayoutService);
  /** Phone lists: the one item whose details are open ('s' signal, 'n' news, 'e' event + id). */
  protected readonly expanded = signal<string | null>(null);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  protected readonly i = { refresh: faArrowsRotate, scan: faMagnifyingGlassChart, chevron: faChevronDown };
  protected readonly tabs: { id: Tab; label: string; short: string }[] = [
    { id: 'signals', label: 'Signals', short: 'Signals' }, { id: 'news', label: 'News & sentiment', short: 'News' }, { id: 'events', label: 'Events', short: 'Events' },
  ];

  protected readonly tab = signal<Tab>('signals');
  protected readonly signals = signal<Signal[]>([]);
  protected readonly news = signal<NewsItem[]>([]);
  protected readonly sentiment = signal<Sentiment[]>([]);
  protected readonly instruments = signal<InstrumentDto[]>([]);
  protected readonly scanTarget = signal('');
  protected readonly scanning = signal(false);
  private readonly symbols = computed(() => new Map(this.instruments().map((i) => [i.instrumentId, i.symbol])));

  constructor() {
    void this.refresh();
    void firstValueFrom(this.api.instruments()).then((i) => this.instruments.set(i)).catch(() => undefined);
    // New signals arrive with events; news with NEWS_* events or on the periodic refresh
    let lastEvent = 0;
    effect(() => {
      const top = this.store.events()[0]?.eventId ?? 0;
      if (top !== lastEvent) {
        lastEvent = top;
        untracked(() => void this.refresh());
      }
    });
    const timer = setInterval(() => void this.refresh(), 60000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected selectTab(t: Tab): void {
    this.tab.set(t);
    this.expanded.set(null);
  }

  protected toggle(key: string): void { this.expanded.update((open) => (open === key ? null : key)); }

  protected count(t: Tab): number {
    return t === 'signals' ? this.signals().length : t === 'news' ? this.news().length : this.store.events().length;
  }

  protected async refresh(): Promise<void> {
    const [signals, news, sentiment] = await Promise.allSettled([
      firstValueFrom(this.api.signals()), firstValueFrom(this.api.news(200)), firstValueFrom(this.api.sentiment()),
    ]);
    if (signals.status === 'fulfilled') this.signals.set(signals.value);
    if (news.status === 'fulfilled') this.news.set(news.value);
    if (sentiment.status === 'fulfilled') this.sentiment.set(sentiment.value.filter((s) => s.netSentimentScore !== 0 || s.tradingHaltActive));
  }

  protected async scan(): Promise<void> {
    this.scanning.set(true);
    try {
      await firstValueFrom(this.api.scanNews(this.scanTarget() ? Number(this.scanTarget()) : null));
      this.toasts.show('info', 'News scan requested', 'The news worker will store results shortly.');
    } catch (e) {
      this.toasts.show('danger', 'Scan request failed', apiErrorText(e));
    } finally {
      this.scanning.set(false);
    }
  }

  protected symbol(id: number): string { return this.symbols().get(id) ?? `#${id}`; }
  protected accountName(id: number | null): string {
    return id === null ? '—' : (this.store.profiles().get(id)?.accountName ?? `#${id}`);
  }
  protected urls(n: NewsItem): string[] {
    try { return (JSON.parse(n.sourceUrls) as string[]).filter((u) => /^https?:\/\//.test(u)).slice(0, 3); } catch { return []; }
  }
  protected bias(b: keyof typeof BIAS_BADGE) { return BIAS_BADGE[b]; }
  protected impact(l: ImpactLevel) { return IMPACT_BADGE[l]; }
  protected severity(s: Severity) { return SEVERITY_BADGE[s]; }
  protected signalBadge(s: string) { return SIGNAL_BADGE[s] ?? 'text-bg-secondary'; }
}
