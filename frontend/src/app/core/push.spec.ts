import { TestBed } from '@angular/core/testing';
import { SwPush } from '@angular/service-worker';
import { NEVER, of } from 'rxjs';
import { ApiService } from './api';
import { PushService, PushServiceUnreachable } from './push';

const kinds = { critical: true, entry: false, exit: true, info: false };

describe('PushService', () => {
  let subscribed: unknown[];
  let requestSubscription: () => Promise<PushSubscription>;

  beforeEach(() => {
    subscribed = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: SwPush, useValue: { isEnabled: true, subscription: NEVER, requestSubscription: () => requestSubscription(), unsubscribe: async () => undefined } },
        {
          provide: ApiService,
          useValue: {
            pushConfig: () => of({ publicKey: 'BPub' }),
            subscribePush: (body: unknown) => { subscribed.push(body); return of({ id: 7 }); },
          },
        },
      ],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('registers the browser subscription keys, a device label and the chosen alerts', async () => {
    requestSubscription = async () =>
      ({ endpoint: 'https://fcm.googleapis.com/fcm/send/abc', toJSON: () => ({ keys: { p256dh: 'BKey', auth: 'secret' } }) }) as unknown as PushSubscription;
    const push = TestBed.inject(PushService);
    await push.enable(kinds);
    expect(subscribed).toEqual([{ endpoint: 'https://fcm.googleapis.com/fcm/send/abc', p256dh: 'BKey', auth: 'secret', deviceLabel: push.deviceLabel(), kinds }]);
  });

  it('gives up with a clear error when the browser never reaches its push service', async () => {
    vi.useFakeTimers();
    requestSubscription = () => new Promise(() => undefined);
    const attempt = TestBed.inject(PushService).enable(kinds);
    const failed = expect(attempt).rejects.toBeInstanceOf(PushServiceUnreachable);
    await vi.advanceTimersByTimeAsync(30_000);
    await failed;
    expect(subscribed).toEqual([]);
  });
});
