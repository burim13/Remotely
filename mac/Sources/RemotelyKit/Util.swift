import Foundation

/// A one-shot result that can be awaited with a timeout and is cancellation-aware (built on AsyncStream so a cancelled wait always returns).
final class Promise<T: Sendable>: @unchecked Sendable {
    private let stream: AsyncStream<Result<T, Error>>
    private let continuation: AsyncStream<Result<T, Error>>.Continuation
    private let lock = NSLock()
    private var done = false

    init() {
        var c: AsyncStream<Result<T, Error>>.Continuation!
        stream = AsyncStream(bufferingPolicy: .bufferingNewest(1)) { c = $0 }
        continuation = c
    }

    var isCompleted: Bool { lock.lock(); defer { lock.unlock() }; return done }

    func succeed(_ value: T) { complete(.success(value)) }
    func fail(_ error: Error) { complete(.failure(error)) }

    private func complete(_ result: Result<T, Error>) {
        lock.lock()
        let first = !done
        done = true
        lock.unlock()
        guard first else { return }
        continuation.yield(result)
        continuation.finish()
    }

    func value(timeout: TimeInterval) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask {
                var iterator = self.stream.makeAsyncIterator()
                guard let result = await iterator.next() else { throw CancellationError() }
                return try result.get()
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw RemotelyError.io("Timed out.")
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }
}
