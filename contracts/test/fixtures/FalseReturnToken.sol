// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

/// @notice Test fixture whose transferFrom explicitly reports failure.
/// @dev It is intentionally not a complete ERC-20 implementation. SafeERC20
///      must turn the false return value into a revert instead of recording a
///      payment that did not happen.
contract FalseReturnToken {
    function transferFrom(address, address, uint256) external pure returns (bool) {
        return false;
    }
}
