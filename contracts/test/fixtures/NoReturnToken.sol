// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

/// @notice Minimal legacy-style token whose approve and transferFrom return no data.
/// @dev This fixture transfers real balances. SafeERC20 treats an empty successful
///      return as success for compatibility with deployed non-standard tokens.
contract NoReturnToken {
    error InsufficientBalance(address account, uint256 balance, uint256 required);
    error InsufficientAllowance(address spender, uint256 allowance, uint256 required);

    mapping(address account => uint256 amount) public balanceOf;
    mapping(address owner => mapping(address spender => uint256 amount)) public allowance;

    function mint(address to, uint256 amount) external {
        balanceOf[to] += amount;
    }

    function approve(address spender, uint256 amount) external {
        allowance[msg.sender][spender] = amount;
    }

    function transferFrom(address from, address to, uint256 amount) external {
        uint256 currentAllowance = allowance[from][msg.sender];
        if (currentAllowance < amount) {
            revert InsufficientAllowance(msg.sender, currentAllowance, amount);
        }

        uint256 currentBalance = balanceOf[from];
        if (currentBalance < amount) {
            revert InsufficientBalance(from, currentBalance, amount);
        }

        if (currentAllowance != type(uint256).max) {
            allowance[from][msg.sender] = currentAllowance - amount;
        }

        balanceOf[from] = currentBalance - amount;
        balanceOf[to] += amount;
    }
}
