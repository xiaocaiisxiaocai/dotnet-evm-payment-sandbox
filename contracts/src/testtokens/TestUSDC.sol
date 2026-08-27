// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";
import {ERC20Permit} from "@openzeppelin/contracts/token/ERC20/extensions/ERC20Permit.sol";

/// @title TestUSDC
/// @notice Six-decimal ERC-20 with ERC-2612 permit support for local/testnet use.
/// @dev TEST ONLY: anyone can mint any amount. This deliberate shortcut makes
///      the contract unsuitable for production or for representing real USDC.
contract TestUSDC is ERC20, ERC20Permit {
    constructor() ERC20("Test USDC", "tUSDC") ERC20Permit("Test USDC") {}

    /// @dev Decimals affect display only. All contract arithmetic still uses
    ///      integer atomic units, where 1 tUSDC is represented as 1_000_000.
    function decimals() public pure override returns (uint8) {
        return 6;
    }

    /// @notice Creates test balance without access control.
    /// @dev TEST ONLY. Never copy this mint policy into a production token.
    function mint(address to, uint256 amount) external {
        _mint(to, amount);
    }
}
