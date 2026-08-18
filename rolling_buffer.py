"""
RollingBuffer: a fixed-capacity circular buffer for multi-channel time
series data, used to back the live plot display. Once capacity is
reached, the oldest samples are overwritten by new ones.
"""

import numpy as np


class RollingBuffer:
    def __init__(self, n_channels: int, capacity: int):
        self.n_channels = n_channels
        self.capacity = capacity
        self.buf = np.zeros((n_channels, capacity), dtype=np.float64)
        self.write_idx = 0
        self.filled = 0

    def reset(self):
        self.buf.fill(0.0)
        self.write_idx = 0
        self.filled = 0

    def push(self, data: np.ndarray):
        """Append data with shape (n_channels, n_new) to the buffer."""
        n_new = data.shape[1]
        if n_new == 0:
            return
        if n_new >= self.capacity:
            self.buf[:, :] = data[:, -self.capacity:]
            self.write_idx = 0
            self.filled = self.capacity
            return

        end = self.write_idx + n_new
        if end <= self.capacity:
            self.buf[:, self.write_idx:end] = data
        else:
            first_part = self.capacity - self.write_idx
            self.buf[:, self.write_idx:] = data[:, :first_part]
            self.buf[:, :end - self.capacity] = data[:, first_part:]
        self.write_idx = end % self.capacity
        self.filled = min(self.capacity, self.filled + n_new)

    def get_last(self, n: int) -> np.ndarray:
        """Return the last n samples per channel, oldest first. Returns
        fewer than n if the buffer has not filled up that much yet."""
        n = min(n, self.filled)
        if n == 0:
            return np.zeros((self.n_channels, 0))
        idx = (self.write_idx - n) % self.capacity
        if idx + n <= self.capacity:
            return self.buf[:, idx:idx + n]
        first_part = self.capacity - idx
        return np.concatenate([self.buf[:, idx:], self.buf[:, :n - first_part]], axis=1)
